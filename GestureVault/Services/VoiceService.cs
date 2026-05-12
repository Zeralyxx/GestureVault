using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Speech.Recognition;
using System.Text.RegularExpressions;
using Windows.Storage;

namespace GestureVault.Services
{
    public class PassphraseVerifiedEventArgs : EventArgs
    {
        public string MatchedPhrase { get; }
        public PassphraseVerifiedEventArgs(string phrase) => MatchedPhrase = phrase;
    }

    public class VerificationFailedEventArgs : EventArgs
    {
        public string Reason { get; }
        public VerificationFailedEventArgs(string reason) => Reason = reason;
    }

    public class VoiceService : IDisposable
    {
        // ── Events ────────────────────────────────────────────────────────────
        public event EventHandler<PassphraseVerifiedEventArgs>? PassphraseVerified;
        public event EventHandler<VerificationFailedEventArgs>? VerificationFailed;

        // ── Internal ──────────────────────────────────────────────────────────
        private readonly SpeechRecognitionEngine _recognizer;
        private readonly DispatcherQueue _dispatcher;
        private bool _disposed = false;
        private string _expectedPassphrase = string.Empty;
        private bool _passphraseLoaded = false;

        public VoiceService(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;

            // ── Recognizer ────────────────────────────────────────────────────
            _recognizer = new SpeechRecognitionEngine();
            _recognizer.SetInputToDefaultAudioDevice();
            _recognizer.BabbleTimeout = TimeSpan.FromSeconds(3);   // was Zero — tolerate noise before speech
            _recognizer.InitialSilenceTimeout = TimeSpan.FromSeconds(8);  // was 6
            _recognizer.EndSilenceTimeout = TimeSpan.FromSeconds(2);  // was 1.5
            _recognizer.EndSilenceTimeoutAmbiguous = TimeSpan.FromSeconds(1.8); // was 1.2

            LoadExpectedPassphrase();

            ConfigureRecognitionGrammars();

            _recognizer.SpeechRecognized += OnSpeechRecognized;
            _recognizer.SpeechRecognitionRejected += OnSpeechRejected;

            // When recognition completes with no result (pure silence/timeout)
            _recognizer.RecognizeCompleted += (s, e) =>
            {
                if (e.Result == null && !e.Cancelled && !_disposed)
                {
                    _dispatcher.TryEnqueue(() =>
                        VerificationFailed?.Invoke(this,
                            new VerificationFailedEventArgs("No speech detected. Please try again.")));
                }
            };
        }

        // ── Passphrase loading ────────────────────────────────────────────────
        private void LoadExpectedPassphrase()
        {
            var settings = ApplicationData.Current.LocalSettings;
            _expectedPassphrase = string.Empty;
            _passphraseLoaded = false;

            string? enc = settings.Values["RegisteredPassphraseEnc"] as string;
            System.Diagnostics.Debug.WriteLine(
                $"[Passphrase] enc exists: {!string.IsNullOrEmpty(enc)} | " +
                $"plain exists: {!string.IsNullOrEmpty(settings.Values["RegisteredPassphrase"] as string)} | " +
                $"SessionState.MasterPassword empty: {string.IsNullOrEmpty(SessionState.MasterPassword)}");

            if (!string.IsNullOrEmpty(enc))
            {
                try
                {
                    string? dec = EncryptionService.DecryptString(enc, SessionState.MasterPassword);
                    if (!string.IsNullOrWhiteSpace(dec))
                    {
                        _expectedPassphrase = dec.Trim();
                        _passphraseLoaded = true;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Passphrase] decrypt failed: {ex.Message}");
                }
            }

            string? plain = settings.Values["RegisteredPassphrase"] as string;
            if (!string.IsNullOrWhiteSpace(plain))
            {
                _expectedPassphrase = plain.Trim();
                _passphraseLoaded = true;
                System.Diagnostics.Debug.WriteLine("[Passphrase] loaded legacy plaintext passphrase");
            }
        }

        private void ConfigureRecognitionGrammars()
        {
            _recognizer.UnloadAllGrammars();
            _recognizer.LoadGrammar(new DictationGrammar());

            if (!_passphraseLoaded)
                return;

            var choices = new Choices();
            foreach (string phrase in GetPassphraseVariants(_expectedPassphrase))
                choices.Add(phrase);

            var builder = new GrammarBuilder(choices)
            {
                Culture = _recognizer.RecognizerInfo.Culture
            };

            _recognizer.LoadGrammar(new Grammar(builder));
        }

        // ── Public API ────────────────────────────────────────────────────────
        public void StartListening()
        {
            if (_disposed) return;

            LoadExpectedPassphrase();
            ConfigureRecognitionGrammars();

            if (!_passphraseLoaded)
            {
                _dispatcher.TryEnqueue(() =>
                    VerificationFailed?.Invoke(this,
                        new VerificationFailedEventArgs("Voice passphrase could not be loaded. Please sign in again.")));
                return;
            }

            try { _recognizer.RecognizeAsyncCancel(); } catch { }
            try { _recognizer.RecognizeAsync(RecognizeMode.Single); }
            catch
            {
                _dispatcher.TryEnqueue(() =>
                    VerificationFailed?.Invoke(this,
                        new VerificationFailedEventArgs("Could not start voice recognition. Please try again.")));
            }
        }

        public void StopListening()
        {
            if (_disposed) return;
            try
            {
                _recognizer.RecognizeAsyncCancel();
            }
            catch { }
        }

        public void RefreshPassphrase() => LoadExpectedPassphrase();

        // ── Recognition handlers ──────────────────────────────────────────────
        private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
        {
            if (_disposed) return;

            string recognized = e.Result.Text.Trim();
            string matchedPhrase = FindMatchingRecognizedPhrase(e.Result);

            // Reject very low-confidence results outright unless an alternate
            // transcript matched the registered phrase.
            if (e.Result.Confidence < 0.3f)
            {
                if (!string.IsNullOrEmpty(matchedPhrase))
                {
                    FireVerificationResult(success: true, "Access granted.", matchedPhrase);
                    return;
                }

                FireVerificationResult(success: false, "Could not hear you clearly. Please try again.", recognized);
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[Voice] Recognition confidence: {e.Result.Confidence:F2}");

            if (!string.IsNullOrEmpty(matchedPhrase))
                FireVerificationResult(success: true, "Access granted.", matchedPhrase);
            else
                FireVerificationResult(success: false, "Phrase not recognized. Please try again.", recognized);
        }

        private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
        {
            if (_disposed) return;
            FireVerificationResult(success: false, "Could not hear you clearly. Please try again.", string.Empty);
        }

        private void FireVerificationResult(bool success, string message, string recognized)
        {
            try { _recognizer.RecognizeAsyncCancel(); } catch { }

            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed) return;
                if (success)
                    PassphraseVerified?.Invoke(this, new PassphraseVerifiedEventArgs(string.Empty));
                else
                    VerificationFailed?.Invoke(this, new VerificationFailedEventArgs(message));
            });
        }

        // ── Matching ──────────────────────────────────────────────────────────
        private bool IsMatch(string recognized)
        {
            if (!_passphraseLoaded || string.IsNullOrWhiteSpace(_expectedPassphrase)) return false;

            string norm1 = Normalize(_expectedPassphrase);
            string norm2 = Normalize(recognized);

            var expWords = norm1.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var recWords = norm2.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Word count must be within ±1
            if (Math.Abs(expWords.Length - recWords.Length) > 2) return false;

            // Tier 1: exact normalized match
            if (string.Equals(norm1, norm2, StringComparison.OrdinalIgnoreCase)) return true;

            // Tier 2: whole-phrase character similarity >= 85%
            if (Similarity(norm1, norm2) >= 0.85) return true;

            // Tier 3: every word must positionally match >= 80%
            if (expWords.Length == recWords.Length && PositionalMatch(expWords, recWords, 0.80))
                return true;

            // Tier 4: short passphrases need speech-aware word matching.
            // This catches common transcripts like "open volt" for "open vault".
            if (expWords.Length == recWords.Length && SpeechAwarePositionalMatch(expWords, recWords))
                return true;

            return false;
        }

        private string FindMatchingRecognizedPhrase(RecognitionResult result)
        {
            var candidates = new List<string> { result.Text };
            candidates.AddRange(result.Alternates.Select(a => a.Text));

            return candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(IsMatch) ?? string.Empty;
        }

        private static bool PositionalMatch(string[] exp, string[] rec, double threshold)
        {
            for (int i = 0; i < exp.Length; i++)
                if (Similarity(exp[i], rec[i]) < threshold) return false;
            return true;
        }

        private static bool SpeechAwarePositionalMatch(string[] exp, string[] rec)
        {
            for (int i = 0; i < exp.Length; i++)
                if (!SpeechWordsMatch(exp[i], rec[i])) return false;
            return true;
        }

        private static bool SpeechWordsMatch(string expected, string recognized)
        {
            if (expected == recognized) return true;
            if (GetSpeechAliases(expected).Contains(recognized)) return true;
            if (Similarity(expected, recognized) >= 0.72) return true;

            return expected.Length >= 4
                && recognized.Length >= 4
                && Soundex(expected) == Soundex(recognized);
        }

        private static double Similarity(string a, string b)
        {
            if (a == b) return 1.0;
            if (a.Length == 0 || b.Length == 0) return 0.0;
            return 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
        }

        private static int Levenshtein(string s, string t)
        {
            int m = s.Length, n = t.Length;
            int[] prev = new int[n + 1], curr = new int[n + 1];
            for (int j = 0; j <= n; j++) prev[j] = j;
            for (int i = 1; i <= m; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= n; j++)
                {
                    int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                Array.Copy(curr, prev, n + 1);
            }
            return prev[n];
        }

        private static string Normalize(string s)
        {
            string lettersAndSpaces = Regex.Replace(s.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
            var words = lettersAndSpaces
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w is not ("a" or "an" or "the"));

            return string.Join(" ", words);
        }

        private static IEnumerable<string> GetPassphraseVariants(string passphrase)
        {
            string normalized = Normalize(passphrase);

            if (!string.IsNullOrWhiteSpace(passphrase))
                yield return passphrase.Trim();

            if (!string.IsNullOrWhiteSpace(normalized)
                && !string.Equals(passphrase.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
                yield return normalized;

            var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 2)
                yield return $"{words[0]} the {words[1]}";

            foreach (string variant in BuildAliasVariants(words))
                yield return variant;
        }

        private static IEnumerable<string> BuildAliasVariants(string[] words)
        {
            if (words.Length == 0)
                yield break;

            for (int i = 0; i < words.Length; i++)
            {
                foreach (string alias in GetSpeechAliases(words[i]))
                {
                    var copy = words.ToArray();
                    copy[i] = alias;
                    yield return string.Join(" ", copy);

                    if (copy.Length == 2)
                        yield return $"{copy[0]} the {copy[1]}";
                }
            }
        }

        private static IEnumerable<string> GetSpeechAliases(string word) => word switch
        {
            "vault" => new[] { "volt", "vote", "fault" },
            "open" => new[] { "opened", "hoping" },
            _ => Array.Empty<string>()
        };

        private static string Soundex(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return string.Empty;

            char first = char.ToUpperInvariant(word[0]);
            var codes = word.ToUpperInvariant().Select(SoundexCode).Where(c => c != '0').ToList();
            var deduped = new List<char>();

            foreach (char code in codes)
            {
                if (deduped.Count == 0 || deduped[^1] != code)
                    deduped.Add(code);
            }

            string tail = new string(deduped.Skip(1).Take(3).ToArray()).PadRight(3, '0');
            return first + tail;
        }

        private static char SoundexCode(char c) => c switch
        {
            'B' or 'F' or 'P' or 'V' => '1',
            'C' or 'G' or 'J' or 'K' or 'Q' or 'S' or 'X' or 'Z' => '2',
            'D' or 'T' => '3',
            'L' => '4',
            'M' or 'N' => '5',
            'R' => '6',
            _ => '0'
        };

        // ── Cleanup ───────────────────────────────────────────────────────────
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _recognizer.SpeechRecognized -= OnSpeechRecognized;
                _recognizer.SpeechRecognitionRejected -= OnSpeechRejected;
                _recognizer.RecognizeAsyncCancel();
                _recognizer.Dispose();
            }
            catch { }
        }
    }
}

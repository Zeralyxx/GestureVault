using Microsoft.UI.Dispatching;
using System;
using System.Speech.Recognition;
using System.Speech.Synthesis;
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
        private readonly SpeechSynthesizer _synthesizer;
        private readonly DispatcherQueue _dispatcher;
        private bool _isListening = false;

        public VoiceService(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;

            // ── Synthesizer (TTS) ─────────────────────────────────────────────
            _synthesizer = new SpeechSynthesizer();
            _synthesizer.SetOutputToDefaultAudioDevice();
            _synthesizer.Rate = 0; // normal speed

            // ── Recognizer ────────────────────────────────────────────────────
            _recognizer = new SpeechRecognitionEngine();
            _recognizer.SetInputToDefaultAudioDevice();

            LoadGrammar();

            // Wire recognition events
            _recognizer.SpeechRecognized += OnSpeechRecognized;
            _recognizer.SpeechRecognitionRejected += OnSpeechRejected;
        }

        // ── Grammar ───────────────────────────────────────────────────────────
        private void LoadGrammar()
        {
            _recognizer.UnloadAllGrammars();

            // Read passphrase saved during registration
            var settings = ApplicationData.Current.LocalSettings;
            string passphrase = settings.Values["RegisteredPassphrase"] as string
                                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(passphrase))
            {
                // Fallback: accept anything so the app doesn't crash before registration
                _recognizer.LoadGrammar(new DictationGrammar());
                return;
            }

            // Scope recognition to the exact registered phrase only
            var builder = new GrammarBuilder(passphrase);
            builder.Culture = _recognizer.RecognizerInfo.Culture;
            var grammar = new Grammar(builder);
            _recognizer.LoadGrammar(grammar);
        }

        // ── Public API ────────────────────────────────────────────────────────
        public void StartListening()
        {
            if (_isListening) return;
            _isListening = true;

            _synthesizer.SpeakAsync("Listening");
            _recognizer.RecognizeAsync(RecognizeMode.Single);
        }

        public void StopListening()
        {
            if (!_isListening) return;
            _isListening = false;
            _recognizer.RecognizeAsyncStop();
        }

        // Reload grammar — call this after registration saves a new passphrase
        public void RefreshPassphrase() => LoadGrammar();

        // ── Recognition handlers ──────────────────────────────────────────────
        private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
        {
            _isListening = false;
            string matched = e.Result.Text;

            _synthesizer.SpeakAsync("Access granted");

            _dispatcher.TryEnqueue(() =>
            {
                PassphraseVerified?.Invoke(this, new PassphraseVerifiedEventArgs(matched));
            });
        }

        private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
        {
            _isListening = false;

            _synthesizer.SpeakAsync("Verification failed, please try again");

            _dispatcher.TryEnqueue(() =>
            {
                VerificationFailed?.Invoke(this,
                    new VerificationFailedEventArgs("Phrase not recognized. Please try again."));
            });
        }

        // ── Cleanup ───────────────────────────────────────────────────────────
        public void Dispose()
        {
            _recognizer.SpeechRecognized -= OnSpeechRecognized;
            _recognizer.SpeechRecognitionRejected -= OnSpeechRejected;
            _recognizer.Dispose();
            _synthesizer.Dispose();
        }
    }
}
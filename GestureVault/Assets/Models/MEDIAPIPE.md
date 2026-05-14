# MediaPipe Gesture Recognizer

Option A uses MediaPipe's built-in Gesture Recognizer through `Tools/mediapipe_gesture_recognizer.py`.

Put the official task model here:

```text
Assets/Models/gesture_recognizer.task
```

Install the Python dependencies on the demo machine:

```powershell
python -m pip install -r Tools/mediapipe-requirements.txt
```

When both Python dependencies and `gesture_recognizer.task` are available, `GestureService` uses MediaPipe first.
If they are missing, the app falls back to `GestureModel.zip` and then to the OpenCV heuristic.

MediaPipe labels are mapped into GestureVault labels:

- `Open_Palm` -> `OPEN_HAND`
- `Closed_Fist` -> `FIST`
- `Pointing_Up` -> `POINT`
- `Thumb_Up` -> `THUMB_UP`
- `Thumb_Down` -> `THUMB_DOWN`
- `Victory` -> `VICTORY`
- `ILoveYou` -> `I_LOVE_YOU`
- `None` -> no gesture

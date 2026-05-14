import base64
import sys

import cv2
import mediapipe as mp
import numpy as np


def normalize_label(label):
    mapping = {
        "Open_Palm": "OPEN_HAND",
        "Closed_Fist": "FIST",
        "Pointing_Up": "POINT",
        "Thumb_Up": "THUMB_UP",
        "Thumb_Down": "THUMB_DOWN",
        "Victory": "VICTORY",
        "ILoveYou": "I_LOVE_YOU",
        "None": "NO_HAND",
    }
    return mapping.get(label, "NO_HAND")


def choose_gesture(categories):
    if not categories:
        return "NO_HAND", 0.0

    normalized = [
        (normalize_label(category.category_name), float(category.score), category.category_name)
        for category in categories
    ]

    specific = [
        item for item in normalized
        if item[0] not in ("NO_HAND", "OPEN_HAND")
    ]
    if specific:
        best_specific = max(specific, key=lambda item: item[1])
        if best_specific[1] >= 0.35:
            return best_specific[0], best_specific[1]

    non_open = [item for item in normalized if item[0] not in ("NO_HAND", "OPEN_HAND")]
    if non_open:
        best = max(non_open, key=lambda item: item[1])
    else:
        best = max(normalized, key=lambda item: item[1])

    if best[0] != "OPEN_HAND" and best[1] >= 0.55:
        return best[0], best[1]

    return "NO_HAND", 0.0


def distance(a, b):
    return ((a.x - b.x) ** 2 + (a.y - b.y) ** 2 + (a.z - b.z) ** 2) ** 0.5


def finger_extended(landmarks, tip, pip, mcp):
    wrist = landmarks[0]
    tip_distance = distance(landmarks[tip], wrist)
    pip_distance = distance(landmarks[pip], wrist)
    mcp_distance = distance(landmarks[mcp], wrist)

    return tip_distance > pip_distance + 0.035 and tip_distance > mcp_distance + 0.08


def finger_folded(landmarks, tip, pip, mcp):
    wrist = landmarks[0]
    tip_distance = distance(landmarks[tip], wrist)
    pip_distance = distance(landmarks[pip], wrist)
    mcp_distance = distance(landmarks[mcp], wrist)

    return tip_distance < pip_distance + 0.02 or tip_distance < mcp_distance + 0.08


def thumb_extended(landmarks, handedness):
    tip = landmarks[4]
    ip = landmarks[3]
    mcp = landmarks[2]
    wrist = landmarks[0]

    horizontal = abs(tip.x - mcp.x) > 0.08
    away_from_palm = abs(tip.x - wrist.x) > abs(ip.x - wrist.x) + 0.025
    thumbs_up_shape = tip.y < ip.y < mcp.y and abs(tip.x - mcp.x) < 0.12

    return horizontal and away_from_palm or thumbs_up_shape


def classify_landmarks(landmarks, handedness):
    if not landmarks:
        return None

    index = finger_extended(landmarks, 8, 6, 5)
    middle = finger_extended(landmarks, 12, 10, 9)
    ring = finger_extended(landmarks, 16, 14, 13)
    pinky = finger_extended(landmarks, 20, 18, 17)

    index_folded = finger_folded(landmarks, 8, 6, 5)
    middle_folded = finger_folded(landmarks, 12, 10, 9)
    ring_folded = finger_folded(landmarks, 16, 14, 13)
    pinky_folded = finger_folded(landmarks, 20, 18, 17)
    thumb = thumb_extended(landmarks, handedness)

    extended_count = sum([index, middle, ring, pinky])
    folded_count = sum([index_folded, middle_folded, ring_folded, pinky_folded])

    if folded_count >= 3 and extended_count <= 1:
        if thumb and landmarks[4].y < landmarks[3].y < landmarks[2].y:
            return "THUMB_UP", 0.82
        if thumb and landmarks[4].y > landmarks[3].y > landmarks[2].y:
            return "THUMB_DOWN", 0.82
        return "FIST", 0.86

    if index and middle and ring_folded and pinky_folded:
        return "VICTORY", 0.88

    if index and middle_folded and ring_folded and pinky_folded:
        return "POINT", 0.86

    if extended_count >= 4 and folded_count == 0:
        return "OPEN_HAND", 0.84

    return None


def main():
    if len(sys.argv) < 2:
        print("ERROR Missing model path", flush=True)
        return 1

    model_path = sys.argv[1]
    BaseOptions = mp.tasks.BaseOptions
    GestureRecognizer = mp.tasks.vision.GestureRecognizer
    GestureRecognizerOptions = mp.tasks.vision.GestureRecognizerOptions
    RunningMode = mp.tasks.vision.RunningMode

    options = GestureRecognizerOptions(
        base_options=BaseOptions(model_asset_path=model_path),
        running_mode=RunningMode.IMAGE,
        num_hands=1,
        min_hand_detection_confidence=0.45,
        min_hand_presence_confidence=0.45,
        min_tracking_confidence=0.45,
    )

    with GestureRecognizer.create_from_options(options) as recognizer:
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue

            try:
                image_bytes = base64.b64decode(line)
                encoded = np.frombuffer(image_bytes, dtype=np.uint8)
                bgr = cv2.imdecode(encoded, cv2.IMREAD_COLOR)
                if bgr is None:
                    print("NO_HAND 0", flush=True)
                    continue

                rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
                mp_image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
                result = recognizer.recognize(mp_image)

                handedness = (
                    result.handedness[0][0].category_name
                    if result.handedness and result.handedness[0]
                    else ""
                )
                landmark_prediction = (
                    classify_landmarks(result.hand_landmarks[0], handedness)
                    if result.hand_landmarks
                    else None
                )

                if landmark_prediction:
                    label, score = landmark_prediction
                    print(f"{label} {score:.4f}", flush=True)
                    continue

                if not result.gestures or not result.gestures[0]:
                    print("NO_HAND 0", flush=True)
                    continue

                label, score = choose_gesture(result.gestures[0])
                print(f"{label} {score:.4f}", flush=True)
            except Exception:
                print("NO_HAND 0", flush=True)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

# Gesture Training Images

Use these folders as the class labels for the gesture model:

```text
Assets/TrainingImages/
  open_hand/
  fist/
  point/
  no_hand/
```

Recommended first pass:

- 100-200 images in `open_hand`
- 100-200 images in `fist`
- 100-200 images in `point`
- 100-200 images in `no_hand`

Capture real webcam conditions:

- face visible and not visible
- hand near the face
- hand centered, left, right, high, and low
- different distances from the camera
- bright, dim, daylight, and indoor lighting
- slightly rotated hands
- different backgrounds

Train the model so it accepts an image byte array column named `ImageSource` and outputs
`PredictedLabel` plus `Score`. Save the trained model as:

```text
Assets/Models/GestureModel.zip
```

# Gesture Model

Put the trained gesture classifier here:

```text
Assets/Models/GestureModel.zip
```

When this file exists, `GestureService` uses it instead of the OpenCV skin/contour fallback.

The runtime input schema expected by the app is:

```csharp
[ColumnName("ImageSource")]
public byte[] ImageSource { get; set; }
```

The output schema expected by the app is:

```csharp
[ColumnName("PredictedLabel")]
public string PredictedLabel { get; set; }

[ColumnName("Score")]
public float[] Score { get; set; }
```

Supported labels:

- `OPEN_HAND`
- `FIST`
- `POINT`
- `NO_HAND`

The model should emit `NO_HAND` when a face, empty frame, or non-gesture object is visible.

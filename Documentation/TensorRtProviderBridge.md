# TensorRT Provider Bridge

MediaFlux hosts TensorRT through `mediaflux-tensorrt.exe`, located beside the application executable. The bridge is the native implementation boundary for the existing AI Provider SDK; MediaFlux retains ownership of model validation, engine compatibility, staging paths, cancellation, telemetry, diagnostics, and restored-frame validation.

The bridge must support these structured command-line operations:

```text
mediaflux-tensorrt.exe build --onnx <model> --engine <staging-engine> --precision <fp16|fp32> --min-shape <NCHW> --opt-shape <NCHW> --max-shape <NCHW>
mediaflux-tensorrt.exe run-directory --engine <engine> --input <png-directory> --output <png-directory> --format png
```

`build` must create a non-empty serialized TensorRT engine at the exact staging path only after a successful build. `run-directory` must preserve each input PNG filename and create one scaled PNG per input. Both commands return zero only after all output writes are complete, write diagnostics as UTF-8, and terminate promptly when MediaFlux requests cancellation.

MediaFlux never reuses an engine unless its model hash, scale, precision, dynamic-shape profile, TensorRT version, CUDA version, GPU identity, and compute capability validate against the current runtime. An invalid or incompatible cache entry is rebuilt before it can be leased for inference.

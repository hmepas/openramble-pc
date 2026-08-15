#!/usr/bin/env python3
"""Build the weight-only int4 Parakeet TDT 0.6B v3 the Windows port ships.

Why this exists. The stock sherpa-onnx int8 export (csukuangfj/...-v3-int8) is *dynamically*
quantized: DynamicQuantizeLinear + MatMulInteger/ConvInteger, so activations are cut to 8 bits
at run time - including in the convolutional front end that sees the spectrogram first. On a
quiet or muffled microphone (a laptop array peaking at -25 dBFS) that model returns nothing or
English filler for Russian speech, while the fp32 export reads the same take correctly. Weight-
only 4-bit block quantization (MatMulNBits, activations stay fp32) keeps fp32 accuracy at the
int8 size and speed. Measured on FLEURS ru dev (40 utterances): int8 WER 7.3%, this 5.5%,
fp32 5.4%.

Output: <out>/encoder.int4.onnx, decoder.onnx, joiner.onnx, tokens.txt and a manifest-files.json
with byteCount + sha256 for each, ready to paste into src/LocalASR/Resources/model-manifest.json.

Requirements: pip install onnx onnxruntime onnx_ir onnxscript
"""
import hashlib, json, os, shutil, sys, time, urllib.request

SOURCE = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3/resolve/main/"
SOURCE_FILES = ["encoder.onnx", "encoder.weights", "decoder.onnx", "joiner.onnx"]
TOKENS_SOURCE = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/tokens.txt"
BLOCK_SIZE = 64          # per-block scale granularity; 64 measured equal to fp32 on ru dev
ACCURACY_LEVEL = 4       # int8 activation kernel inside MatMulNBits: same speed as int8 export


def fetch(url, path):
    if os.path.exists(path):
        return
    print(f"  {os.path.basename(path)} <- {url}")
    with urllib.request.urlopen(url) as r, open(path + ".part", "wb") as f:
        shutil.copyfileobj(r, f, 1 << 20)
    os.rename(path + ".part", path)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    if len(sys.argv) != 3:
        print("usage: quantize-parakeet-int4.py <work-dir> <out-dir>")
        return 64
    work, out = sys.argv[1], sys.argv[2]
    os.makedirs(work, exist_ok=True)
    os.makedirs(out, exist_ok=True)

    print("fetching the fp32 export")
    for name in SOURCE_FILES:
        fetch(SOURCE + name, os.path.join(work, name))
    fetch(TOKENS_SOURCE, os.path.join(work, "tokens.txt"))

    import onnx
    from onnxruntime.quantization.matmul_nbits_quantizer import (
        DefaultWeightOnlyQuantConfig, MatMulNBitsQuantizer)

    print("quantizing the encoder (MatMul -> MatMulNBits, 4-bit, symmetric, block %d)" % BLOCK_SIZE)
    began = time.time()
    model = onnx.load(os.path.join(work, "encoder.onnx"))  # pulls encoder.weights in
    config = DefaultWeightOnlyQuantConfig(
        block_size=BLOCK_SIZE, is_symmetric=True, bits=4, accuracy_level=ACCURACY_LEVEL)
    quantizer = MatMulNBitsQuantizer(model, algo_config=config)
    quantizer.process()
    encoder_out = os.path.join(out, "encoder.int4.onnx")
    quantizer.model.save_model_to_file(encoder_out, use_external_data_format=False)
    print(f"  {os.path.getsize(encoder_out) / 1e6:.0f} MB in {time.time() - began:.0f} s")

    for name in ["decoder.onnx", "joiner.onnx", "tokens.txt"]:
        shutil.copyfile(os.path.join(work, name), os.path.join(out, name))

    files = []
    for name in ["encoder.int4.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt"]:
        path = os.path.join(out, name)
        files.append({"path": name, "byteCount": os.path.getsize(path), "sha256": sha256(path)})
    with open(os.path.join(out, "manifest-files.json"), "w") as f:
        json.dump(files, f, indent=2)
    print(json.dumps(files, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())

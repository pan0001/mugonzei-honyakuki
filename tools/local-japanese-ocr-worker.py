"""Local NDLOCR-Lite CPU worker. JSONL protocol only; never downloads models."""
import argparse
import json
import os
from pathlib import Path
import re
import sys
import tempfile
from types import SimpleNamespace

MAX_IMAGE_BYTES = 12 * 1024 * 1024
MAX_TEXT = 32000
MAX_REQUEST = 16384


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", required=True)
    runtime = Path(parser.parse_args().runtime).resolve(strict=True)
    # Keep a dedicated protocol descriptor. Even native library stdout goes to stderr.
    protocol = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", newline="\n", buffering=1)
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    sys.stdin.reconfigure(encoding="utf-8", errors="strict")

    def emit(value):
        protocol.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")
        protocol.flush()

    # This worker only reads local model/image files. Reject accidental network use.
    def offline(event, _args):
        if event in ("socket.connect", "socket.getaddrinfo"):
            raise PermissionError("The local OCR worker does not use a network.")

    sys.addaudithook(offline)
    os.environ["OMP_NUM_THREADS"] = "2"
    os.environ["OPENBLAS_NUM_THREADS"] = "2"
    os.environ["MKL_NUM_THREADS"] = "2"
    source = runtime / "ndlocr-lite" / "src"
    sys.path.insert(0, str(source))
    try:
        import numpy as np
        from PIL import Image
        import onnxruntime as ort

        # Four cached models should not each consume every logical CPU during VR play.
        original_session = ort.InferenceSession

        def bounded_session(path, sess_options=None, **kwargs):
            options = sess_options if sess_options is not None else ort.SessionOptions()
            options.intra_op_num_threads = 2
            options.inter_op_num_threads = 1
            options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
            kwargs["providers"] = ["CPUExecutionProvider"]
            return original_session(path, options, **kwargs)

        ort.InferenceSession = bounded_session
        import ocr
        from concurrent.futures import ThreadPoolExecutor

        class CpuExecutor(ThreadPoolExecutor):
            def __init__(self, max_workers=None, *args, **kwargs):
                super().__init__(min(2, max_workers or 2), *args, **kwargs)

        ocr.ThreadPoolExecutor = CpuExecutor
        get_detector = ocr.get_detector
        get_recognizer = ocr.get_recognizer
        _run_ocr_on_image_array = ocr._run_ocr_on_image_array

        args = SimpleNamespace(
            device="cpu", enable_tcy=False,
            det_weights=str(source / "model" / "deim-s-1024x1024.onnx"),
            det_classes=str(source / "config" / "ndl.yaml"),
            rec_classes=str(source / "config" / "NDLmoji.yaml"),
            det_score_threshold=0.2, det_conf_threshold=0.25, det_iou_threshold=0.2,
        )
        detector = get_detector(args)
        models = [
            "parseq-ndl-24x256-30-tiny-189epoch-tegaki3-r8data-202604.onnx",
            "parseq-ndl-24x384-50-tiny-300epoch-tegaki3-r8data-202604.onnx",
            "parseq-ndl-24x768-100-tiny-153epoch-tegaki3-r8data-202604.onnx",
        ]
        recognizers = [get_recognizer(args, str(source / "model" / name)) for name in models]
    except Exception as error:
        print("Local OCR initialization failed: " + type(error).__name__, file=sys.stderr)
        emit({"type": "fatal", "code": "runtime"})
        return 1

    Image.MAX_IMAGE_PIXELS = 4_000_000
    image_root = (Path(tempfile.gettempdir()) / "SteamVrTranslator-LocalOCR").resolve()
    emit({"type": "ready", "protocol": 1})
    while True:
        line = sys.stdin.readline(MAX_REQUEST + 1)
        if not line:
            return 0
        if len(line) > MAX_REQUEST or not line.endswith("\n"):
            return 2
        request_id = None
        try:
            request = json.loads(line)
            request_id = request["id"]
            if not isinstance(request_id, str) or not re.fullmatch(r"[0-9a-f]{32}", request_id):
                return 2
            path = Path(request["image_path"]).resolve(strict=True)
            if (path.name != "screenshot.png" or path.parent.parent != image_root
                    or not re.fullmatch(r"[0-9a-f]{32}", path.parent.name)
                    or not 0 < path.stat().st_size <= MAX_IMAGE_BYTES):
                raise ValueError("invalid image path")
            with Image.open(path) as image:
                if (image.format != "PNG" or min(image.size) < 1 or max(image.size) > 2560
                        or image.width * image.height > 4_000_000):
                    raise ValueError("invalid image bounds")
                pixels = np.array(image.convert("RGB"))
        except Exception:
            emit({"id": request_id, "ok": False, "code": "image"})
            continue
        try:
            result = _run_ocr_on_image_array(
                detector, *recognizers, inputname="screenshot.png", img=pixels,
                outputpath=str(path.parent), save_viz=False,
            )
            text = result["text"]
            if not isinstance(text, str):
                raise TypeError("invalid text")
            if len(text) > MAX_TEXT:
                emit({"id": request_id, "ok": False, "code": "too_long"})
            else:
                emit({"id": request_id, "ok": True, "text": text})
        except Exception as error:
            print("Local OCR inference failed: " + type(error).__name__, file=sys.stderr)
            emit({"id": request_id, "ok": False, "code": "inference"})
        finally:
            del pixels


if __name__ == "__main__":
    raise SystemExit(main())

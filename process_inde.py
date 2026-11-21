"""Process RGB TIFF or PNG images with the installed InDe Windows backend."""

import argparse
from contextlib import contextmanager
import io
import os
from pathlib import Path
import struct
import sys
import threading
import zlib
from tempfile import TemporaryDirectory

import cv2
import numpy as np


ICC_TAG = 34675
TIFF_FLAGS = [cv2.IMWRITE_TIFF_COMPRESSION, cv2.IMWRITE_TIFF_COMPRESSION_LZW]


def arguments():
    local = os.environ.get("LOCALAPPDATA")
    backend = Path(local) / "Programs/InDe/InDeBackend" if local else None
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument("--output", type=Path, required=True, help="new output directory")
    parser.add_argument("--backend", type=Path, default=backend, required=backend is None)
    parser.add_argument("--device", choices=("auto", "dml", "cpu"), default="auto")
    parser.add_argument("--ss", type=float, default=0.40, help="star sharpening, 0 to 0.7")
    parser.add_argument("--ash", type=float, default=0.0, help="star halo adjustment, -0.5 to 0.5")
    parser.add_argument("--dn", type=float, default=0.75, help="denoise strength, 0 to 1")
    parser.add_argument("--color", type=float, help="optional separate chroma denoise, 0 to 1")
    parser.add_argument("--iterations", type=int, default=1, choices=range(1, 6))
    parser.add_argument("--overlap", type=float, default=0.20, help="tile overlap, 0 to 0.5")
    parser.add_argument("--desktop", action="store_true", help=argparse.SUPPRESS)
    return parser.parse_args()


def tiff_directory(stream):
    header = stream.read(8)
    order = {b"II": "<", b"MM": ">"}.get(header[:2])
    if order is None or len(header) != 8:
        raise ValueError("Expected a standard TIFF header")
    magic, offset = struct.unpack_from(order + "HI", header, 2)
    if magic != 42:
        raise ValueError("Expected a standard TIFF (Classic TIFF format)")
    stream.seek(offset)
    count, = struct.unpack(order + "H", stream.read(2))
    entries = [stream.read(12) for _ in range(count)]
    next_offset, = struct.unpack(order + "I", stream.read(4))
    return order, entries, next_offset


def source_profile(path):
    with path.open("rb") as stream:
        order, entries, next_offset = tiff_directory(stream)
        if next_offset:
            raise ValueError("Expected a single-page TIFF")
        for entry in entries:
            tag, kind, count, offset = struct.unpack(order + "HHII", entry)
            if tag == 274:
                orientation, = struct.unpack_from(order + "H", entry, 8)
                if orientation != 1:
                    raise ValueError("Export the TIFF with its orientation applied first")
            if tag == ICC_TAG and kind in (1, 7):
                stream.seek(offset)
                profile = stream.read(count)
                if len(profile) != count or count < 128:
                    raise ValueError("Incomplete TIFF ICC profile")
                return profile
    return None


def embed_profile(encoded, profile):
    order, entries, next_offset = tiff_directory(io.BytesIO(encoded))
    data = bytearray(encoded)
    if len(data) % 2:
        data.append(0)
    offset = len(data)
    data.extend(profile)
    if len(data) % 2:
        data.append(0)
    directory_offset = len(data)
    entries.append(struct.pack(order + "HHII", ICC_TAG, 7, len(profile), offset))
    entries.sort(key=lambda entry: struct.unpack_from(order + "H", entry)[0])
    data.extend(struct.pack(order + "H", len(entries)))
    data.extend(b"".join(entries))
    data.extend(struct.pack(order + "I", next_offset))
    struct.pack_into(order + "I", data, 4, directory_offset)
    return data


def decode_tiff(path):
    image = cv2.imdecode(np.fromfile(path, np.uint8), cv2.IMREAD_UNCHANGED)
    if image is None or image.dtype != np.uint16 or image.ndim != 3 or image.shape[2] != 3:
        raise ValueError(f"Expected a readable 16-bit RGB TIFF: {path}")
    return image


def read_image(path):
    data = path.read_bytes()
    png = data.startswith(b"\x89PNG\r\n\x1a\n")
    profile = None
    if png:
        offset = 8
        while offset + 12 <= len(data):
            size, tag = struct.unpack_from(">I4s", data, offset)
            if tag == b"iCCP":
                name, payload = data[offset + 8:offset + 8 + size].split(b"\x00", 1)
                if not name or not payload or payload[0] != 0:
                    raise ValueError("Invalid PNG ICC profile")
                profile = zlib.decompress(payload[1:], bufsize=4096)
                break
            if tag in (b"IDAT", b"IEND"):
                break
            offset += size + 12
    else:
        profile = source_profile(path)
    image = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_UNCHANGED)
    if image is None or image.dtype not in (np.uint8, np.uint16) or image.ndim != 3:
        raise ValueError("Use an 8-bit or 16-bit RGB TIFF or PNG")
    maximum = np.iinfo(image.dtype).max
    if png and image.shape[2] == 4 and np.all(image[:, :, 3] == maximum):
        image = image[:, :, :3]
    if image.shape[2] != 3:
        raise ValueError("Use an opaque RGB TIFF or PNG")
    rgb = np.ascontiguousarray(image[:, :, ::-1], dtype=np.float32)
    rgb /= np.float32(maximum)
    return rgb, profile


def save_tiff(path, image, profile):
    bgr = np.rint(np.clip(image[:, :, ::-1], 0, 1) * 65535).astype(np.uint16)
    ok, encoded = cv2.imencode(".tif", bgr, TIFF_FLAGS)
    if not ok:
        raise RuntimeError(f"Could not encode {path.name}")
    data = encoded.tobytes()
    path.write_bytes(embed_profile(data, profile) if profile is not None else data)
    if not np.array_equal(decode_tiff(path), bgr) or source_profile(path) != profile:
        raise RuntimeError(f"Could not verify pixels and ICC profile in {path.name}")
    print(f"Saved {path.name}", flush=True)


def preview(image, max_width):
    height, width = image.shape[:2]
    if width > max_width:
        image = cv2.resize(image, (max_width, max(1, round(height * max_width / width))),
                           interpolation=cv2.INTER_AREA)
    return np.ascontiguousarray(np.rint(np.clip(image[:, :, ::-1], 0, 1) * 255),
                                dtype=np.uint8)


def write_input_preview(path):
    original, profile = read_image(path)
    height, width = original.shape[:2]
    thumbnail_width = max(1, round(1280 * width / max(width, height)))
    ok, encoded = cv2.imencode(".png", preview(original, thumbnail_width))
    if not ok:
        raise RuntimeError("Could not render the input preview")
    png = encoded.tobytes()
    if profile is not None:
        chunk = b"iCCPInDe source\x00\x00" + zlib.compress(profile)
        png = png[:33] + struct.pack(">I", len(chunk) - 4) + chunk + struct.pack(">I", zlib.crc32(chunk)) + png[33:]
    sys.stdout.buffer.write(struct.pack("<II", width, height))
    sys.stdout.buffer.write(png)


def save_jpeg(path, image, profile):
    ok, encoded = cv2.imencode(".jpg", image, [cv2.IMWRITE_JPEG_QUALITY, 94])
    if not ok:
        raise RuntimeError(f"Could not encode {path.name}")
    data = encoded.tobytes()
    if profile is not None:
        # JPEG ICC profiles are split into numbered APP2 segments.
        chunks = [profile[start:start + 65519] for start in range(0, len(profile), 65519)]
        segments = []
        for index, chunk in enumerate(chunks, start=1):
            payload = b"ICC_PROFILE\x00" + bytes((index, len(chunks))) + chunk
            segments.append(b"\xff\xe2" + struct.pack(">H", len(payload) + 2) + payload)
        data = data[:2] + b"".join(segments) + data[2:]
    path.write_bytes(data)


def save_previews(out, original, composite, denoised, profile):
    save_jpeg(out / "InDe_Composite_Preview.jpg", preview(composite, 1550), profile)
    views = [preview(image, 800) for image in (original, composite, denoised)]
    height, width = views[0].shape[:2]
    panel = np.full((height + 54, width * 3 + 48, 3), (26, 21, 18), dtype=np.uint8)
    labels = ("Original", "InDe composite", "Starless + denoise")
    for index, (label, view) in enumerate(zip(labels, views)):
        x = 12 + index * (width + 12)
        cv2.putText(panel, label, (x, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.73,
                    (245, 242, 240), 1, cv2.LINE_AA)
        panel[42:42 + height, x:x + width] = view
    save_jpeg(out / "InDe_Comparison.jpg", panel, profile)


@contextmanager
def staged_output(destination):
    if destination.exists():
        raise FileExistsError(f"Output directory already exists: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(prefix=".inde-", dir=destination.parent) as folder:
        staging = Path(folder) / "results"
        staging.mkdir()
        yield staging
        staging.rename(destination)


def process(worker, backend, product, image, params):
    print(f"@@stage {product}", flush=True)
    # Refresh the native worker's session cache between models to release GPU memory.
    engine = worker._load_engine(backend / "engine")
    settings = worker._arguments(product, params, False)
    minimum = 512 if product == "conv" else 8
    padded, padding = engine._pad_to_min_window(image, minimum)
    if product == "conv":
        result, layer = engine.run_product(product, padded, settings)
    elif product == "stars":
        result, layer = engine.run_stars_product(padded, settings)
    else:
        result, layer = engine.run_noise_product(padded, settings)
    result = engine._crop_back(result, padding)
    if layer is not None:
        layer = engine._crop_back(layer, padding)
    for pixels in (result, layer) if layer is not None else (result,):
        if pixels.shape != image.shape or not np.isfinite(pixels).all():
            raise RuntimeError(f"Invalid result from InDe {product}")
    return result, layer


def run(options):
    cancelled = threading.Event()
    if options.desktop:
        def listen_for_cancel():
            if os.read(sys.stdin.fileno(), 32).strip() == b"cancel":
                cancelled.set()
        threading.Thread(target=listen_for_cancel, daemon=True).start()

    def check_cancelled():
        if cancelled.is_set():
            raise RuntimeError("处理已取消。")

    backend = options.backend.resolve()
    required = ["tools/inde_worker.py", "tools/inde_runtime.py", "engine/run_models.py"]
    required += [f"engine/models/InDe{name}.onnx" for name in ("Conv", "Stars", "Noise")]
    missing = [file for file in required if not (backend / file).is_file()]
    if missing:
        raise FileNotFoundError(f"InDe backend at {backend} is missing {', '.join(missing)}")
    sys.path.insert(0, str(backend / "tools"))
    import inde_worker as worker

    shared = {"device": options.device, "overlap": options.overlap}
    conv = dict(shared, ss=options.ss, ash=options.ash)
    stars_params = dict(shared, stars=True, unscreen=False)
    noise = dict(shared, dn=options.dn, iterations=options.iterations)
    if options.color is not None:
        noise.update(csep=True, di=options.dn, dc=options.color)
    for product, params in (("conv", conv), ("stars", stars_params), ("noise", noise)):
        worker._arguments(product, params, False)

    original, profile = read_image(options.image)
    with staged_output(options.output) as out:
        check_cancelled()
        sharpened, _ = process(worker, backend, "conv", original, conv)
        check_cancelled()
        save_tiff(out / "01_InDeConv.tif", sharpened, profile)
        check_cancelled()
        starless, stars = process(worker, backend, "stars", sharpened, stars_params)
        check_cancelled()
        save_tiff(out / "02_InDeStars_Starless.tif", starless, profile)
        save_tiff(out / "03_InDeStars_Stars.tif", stars, profile)
        del stars
        check_cancelled()
        denoised, _ = process(worker, backend, "noise", starless, noise)
        check_cancelled()
        print("@@stage export", flush=True)
        save_tiff(out / "04_InDeNoise_Starless.tif", denoised, profile)
        composite = np.clip(sharpened + (denoised - starless), 0, 1)
        save_tiff(out / "05_InDe_Composite.tif", composite, profile)
        save_previews(out, original, composite, denoised, profile)
        check_cancelled()
    print(f"Finished: {options.output.resolve()}", flush=True)


if __name__ == "__main__":
    try:
        if len(sys.argv) == 3 and sys.argv[1] == "--preview-input":
            write_input_preview(Path(sys.argv[2]))
        else:
            run(arguments())
    except (OSError, ValueError, RuntimeError, struct.error, zlib.error) as error:
        sys.exit(str(error))

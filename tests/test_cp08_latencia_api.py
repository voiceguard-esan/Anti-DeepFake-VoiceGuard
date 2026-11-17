import json
import math
import struct
import time
import unittest
import urllib.request
import urllib.error

API_URL = "http://localhost:5000/api/analisis"
NUM_PETICIONES = 22


def generar_wav_prueba(duracion_seg=1, sample_rate=16000):
    num_samples = int(duracion_seg * sample_rate)
    data_bytes = bytearray()
    for i in range(num_samples):
        muestra = int(12000 * math.sin(2.0 * math.pi * 440.0 * (i / sample_rate)))
        data_bytes.extend(struct.pack("<h", muestra))

    num_bytes = len(data_bytes)
    cabecera = struct.pack(
        "<4sI4s4sIHHIIHH4sI",
        b"RIFF", 36 + num_bytes, b"WAVE", b"fmt ", 16, 1, 1,
        sample_rate, sample_rate * 2, 2, 16, b"data", num_bytes
    )
    return cabecera + bytes(data_bytes)


def enviar_wav(wav_bytes):
    boundary = "----VoiceGuardLatenciaBoundary12345"
    cuerpo = (
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="file"; filename="latencia_test.wav"\r\n'
        f"Content-Type: audio/wav\r\n\r\n"
    ).encode("utf-8") + wav_bytes + f"\r\n--{boundary}--\r\n".encode("utf-8")

    req = urllib.request.Request(API_URL, data=cuerpo, method="POST")
    req.add_header("Content-Type", f"multipart/form-data; boundary={boundary}")
    return urllib.request.urlopen(req, timeout=15)


class TestCP08LatenciaAPI(unittest.TestCase):
    """
    CP-08 (HU-04): Medir el tiempo de respuesta extremo a extremo de POST /api/analisis
    con el modelo ONNX integrado sobre 22 peticiones consecutivas (< 10 segundos).
    """

    def test_latencia_22_peticiones_consecutivas(self):
        wav_bytes = generar_wav_prueba()
        tiempos_ms = []

        print(f"\n[CP-08] Ejecutando {NUM_PETICIONES} peticiones consecutivas hacia {API_URL}...")
        for idx in range(1, NUM_PETICIONES + 1):
            inicio = time.perf_counter()
            try:
                with enviar_wav(wav_bytes) as resp:
                    self.assertEqual(resp.status, 200)
                    _ = json.loads(resp.read().decode("utf-8"))
            except urllib.error.URLError as e:
                self.fail(f"Error de conexión en petición {idx}: {e}")
            duracion_ms = (time.perf_counter() - inicio) * 1000.0
            tiempos_ms.append(duracion_ms)

        t_min = min(tiempos_ms)
        t_prom = sum(tiempos_ms) / len(tiempos_ms)
        t_max = max(tiempos_ms)

        print(f"[CP-08] Peticiones exitosas: {len(tiempos_ms)}/{NUM_PETICIONES} (HTTP 200 OK)")
        print(f"[CP-08] Latencia Mínima  : {t_min:.1f} ms")
        print(f"[CP-08] Latencia Promedio: {t_prom:.1f} ms")
        print(f"[CP-08] Latencia Máxima  : {t_max:.1f} ms (Umbral permitido: < 10,000.0 ms)")

        self.assertLess(t_max, 10000.0, "La latencia máxima debe ser inferior a 10 segundos")


if __name__ == "__main__":
    unittest.main(verbosity=2)
import io
import json
import math
import struct
import unittest
import urllib.request
import urllib.error


API_URL = "http://localhost:5000/api/analisis"
WEB_URL = "http://localhost:5173"


def generar_wav_sintetico_en_memoria(duracion_seg=2, sample_rate=16000, freq_hz=440.0):
    """Genera un archivo WAV válido PCM 16-bit mono en memoria para simular subida web."""
    num_samples = int(duracion_seg * sample_rate)
    data_bytes = bytearray()

    for i in range(num_samples):
        t = i / sample_rate
        # Onda senoidal pura (típica señal sintética de prueba)
        muestra = int(16000 * math.sin(2.0 * math.pi * freq_hz * t))
        data_bytes.extend(struct.pack("<h", muestra))

    num_bytes_audio = len(data_bytes)
    byte_rate = sample_rate * 2
    block_align = 2
    chunk_size = 36 + num_bytes_audio

    cabecera = struct.pack(
        "<4sI4s4sIHHIIHH4sI",
        b"RIFF",
        chunk_size,
        b"WAVE",
        b"fmt ",
        16,
        1,
        1,
        sample_rate,
        byte_rate,
        block_align,
        16,
        b"data",
        num_bytes_audio,
    )
    return cabecera + bytes(data_bytes)


def enviar_multipart_wav(url, filename, wav_bytes):
    boundary = "----VoiceGuardWebUploadTestBoundary987654321"
    cuerpo = (
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="file"; filename="{filename}"\r\n'
        f"Content-Type: audio/wav\r\n\r\n"
    ).encode("utf-8") + wav_bytes + f"\r\n--{boundary}--\r\n".encode("utf-8")

    req = urllib.request.Request(url, data=cuerpo, method="POST")
    req.add_header("Content-Type", f"multipart/form-data; boundary={boundary}")
    req.add_header("Origin", WEB_URL)  # Simula petición CORS desde el frontend Vue.js
    return urllib.request.urlopen(req, timeout=10)


class TestCP09SubidaWeb(unittest.TestCase):
    """
    CP-09 (HU-05): Verificar la subida y análisis de archivos WAV desde la interfaz web Vue.js
    hacia el endpoint POST /api/analisis con el modelo ONNX integrado.
    """

    def test_01_disponibilidad_interfaz_web_y_cors(self):
        """Verifica que la aplicación web Vue.js (puerto 5173) esté activa y responda HTTP 200."""
        try:
            with urllib.request.urlopen(WEB_URL, timeout=5) as resp:
                self.assertEqual(resp.status, 200, "La vista web Vue.js debe responder HTTP 200 OK")
                print(f"\n[CP-09.1] Interfaz web operativa en {WEB_URL} -> HTTP {resp.status} OK")
        except urllib.error.URLError as e:
            self.fail(f"No se pudo conectar al frontend en {WEB_URL}. Ejecuta 'npm run dev' primero. Detalle: {e}")

    def test_02_subida_archivo_wav_desde_web_y_diagnostico_onnx(self):
        """Simula el envío de un archivo .wav desde el navegador (con cabecera Origin) hacia /api/analisis."""
        wav_prueba = generar_wav_sintetico_en_memoria(duracion_seg=2, sample_rate=16000, freq_hz=320.0)
        self.assertLessEqual(len(wav_prueba), 1024 * 1024, "El archivo de prueba debe ser <= 1 MB")

        try:
            with enviar_multipart_wav(API_URL, "grabacion_web_prueba.wav", wav_prueba) as resp:
                self.assertEqual(resp.status, 200, "La API debe devolver HTTP 200 OK al subir WAV desde la web")

                # Validar cabecera CORS para permitir lectura desde Vue.js
                cors_header = resp.headers.get("Access-Control-Allow-Origin")
                self.assertIsNotNone(cors_header, "La API debe incluir cabecera CORS Access-Control-Allow-Origin")

                raw_body = resp.read().decode("utf-8")
                data = json.loads(raw_body)

                self.assertIn("resultado", data, "El JSON debe incluir el campo 'resultado'")
                self.assertIn("confianza", data, "El JSON debe incluir el campo 'confianza'")
                self.assertIn(data["resultado"], ["real", "sintetica"], "El diagnóstico debe ser 'real' o 'sintetica'")
                self.assertGreaterEqual(float(data["confianza"]), 0.0)
                self.assertLessEqual(float(data["confianza"]), 1.0)

                print(
                    f"[CP-09.2] Subida web ('grabacion_web_prueba.wav', {len(wav_prueba)} bytes) -> "
                    f"HTTP 200 OK | Resultado: {data['resultado'].upper()} | Confianza: {data['confianza']*100:.1f}%"
                )
        except urllib.error.URLError as e:
            self.fail(f"No se pudo conectar a la API en {API_URL}. Ejecuta 'dotnet run' primero. Detalle: {e}")


if __name__ == "__main__":
    unittest.main(verbosity=2)

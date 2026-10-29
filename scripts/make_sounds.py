"""Генерує звуки тривоги та відбою: синтезована сирена/дзвін + голос RHVoice.

python3 make_sounds.py <голос> <вихідна_папка>
"""
import subprocess
import sys
import wave
from pathlib import Path

import numpy as np

SR = 44100
VOICE = sys.argv[1] if len(sys.argv) > 1 else "anatol"
OUT = Path(sys.argv[2] if len(sys.argv) > 2 else ".")
OUT.mkdir(parents=True, exist_ok=True)
TMP = OUT / "_tmp"
TMP.mkdir(exist_ok=True)

ALERT_TEXT = "Увага! Повітряна тривога! Негайно пройдіть до найближчого укриття."
CLEAR_TEXT = "Увага! Відбій повітряної тривоги."


def tts(text: str, name: str) -> np.ndarray:
    raw = TMP / f"{name}.wav"
    subprocess.run(["RHVoice-test", "-p", VOICE, "-r", "90", "-o", str(raw)],
                   input=text.encode("utf-8"), check=True, capture_output=True)
    res = TMP / f"{name}_44k.wav"
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(raw), "-ac", "1", "-ar", str(SR),
                    "-sample_fmt", "s16", str(res)], check=True)
    with wave.open(str(res)) as w:
        data = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float64) / 32768
    peak = np.max(np.abs(data)) or 1
    return data / peak * 0.9


def silence(sec: float) -> np.ndarray:
    return np.zeros(int(SR * sec))


def fade(x: np.ndarray, fin: float, fout: float) -> np.ndarray:
    x = x.copy()
    n_in, n_out = int(SR * fin), int(SR * fout)
    if n_in:
        x[:n_in] *= np.linspace(0, 1, n_in)
    if n_out:
        x[-n_out:] *= np.linspace(1, 0, n_out)
    return x


def siren(seconds: float = 9.0) -> np.ndarray:
    """Виюча сирена цивільної оборони: частота повільно росте й спадає (період ~4.5 с)."""
    t = np.arange(int(SR * seconds)) / SR
    period = 4.5
    phase_pos = (t % period) / period
    # плавний підйом (60 %) і спад (40 %)
    shape = np.where(phase_pos < 0.6, np.sin(np.pi / 2 * phase_pos / 0.6),
                     np.cos(np.pi / 2 * (phase_pos - 0.6) / 0.4))
    freq = 330 + (780 - 330) * shape
    phase = 2 * np.pi * np.cumsum(freq) / SR
    # багатий тембр ротора сирени: основний тон + гармоніки
    sig = (np.sin(phase) + 0.45 * np.sin(2 * phase) + 0.25 * np.sin(3 * phase) + 0.12 * np.sin(4 * phase))
    sig /= np.max(np.abs(sig))
    return fade(sig * 0.8, 1.2, 1.5)


def chime() -> np.ndarray:
    """Спокійний тритоновий дзвін (до-мі-соль) для відбою."""
    parts = []
    for f in (523.25, 659.25, 783.99):
        t = np.arange(int(SR * 0.9)) / SR
        env = np.exp(-t * 3.2)
        tone = (np.sin(2 * np.pi * f * t) + 0.3 * np.sin(4 * np.pi * f * t)) * env
        parts.append(tone)
    out = np.zeros(int(SR * 2.4))
    for i, p in enumerate(parts):
        s = int(SR * 0.45 * i)
        out[s:s + len(p)] += p
    return out / np.max(np.abs(out)) * 0.7


def save(name: str, x: np.ndarray) -> Path:
    wav = TMP / f"{name}.wav"
    with wave.open(str(wav), "w") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype(np.int16).tobytes())
    mp3 = OUT / f"{name}.mp3"
    # Нормалізація гучності під трансляцію, 44.1 кГц моно 128 кбіт/с (сумісно з MediaPlayer і MCI)
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(wav),
                    "-af", "loudnorm=I=-14:TP=-1.5:LRA=11", "-ar", str(SR), "-ac", "1",
                    "-codec:a", "libmp3lame", "-b:a", "128k", str(mp3)], check=True)
    return mp3


alert_voice = tts(ALERT_TEXT, "alert")
clear_voice = tts(CLEAR_TEXT, "clear")

air_alert = np.concatenate([siren(), silence(0.6), alert_voice, silence(1.2), alert_voice, silence(0.5)])
all_clear = np.concatenate([chime(), silence(0.4), clear_voice, silence(1.2), clear_voice, silence(0.5)])

for p in (save("air-alert", air_alert), save("all-clear", all_clear)):
    print(p)

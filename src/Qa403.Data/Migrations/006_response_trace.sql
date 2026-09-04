-- Frequency-response (chirp) results.
--
-- A response is not a sweep and not an FFT capture, and squeezing it into either table would lose
-- something. A sweep stores N captures of scalars; a response is one capture holding continuous
-- curves. The `spectrum` table is closer, but it holds one magnitude array per channel and this
-- needs five parallel arrays per curve (frequency, magnitude min and max, phase, coherence) plus
-- the corner readings the Measurements table shows -- and one of the three curves, the gain, is a
-- ratio between two inputs and so belongs to no channel at all.
--
-- The arrays are the *decimated* display columns (about 1600 of them), not full-resolution bins.
-- That is deliberate: at the 0.37 Hz setting a full-resolution curve is 262145 bins, the chart can
-- never draw more than a couple of thousand, and the numbers that must not be decimated -- the peak
-- and the -1/-3 dB corners -- are computed at full resolution before decimation and stored here as
-- scalars. `mag_min_db` and `mag_max_db` are both kept for the same reason the spectrum path keeps
-- both: a max-only column redraws a rippled response riding along the top of its own ripple.
--
-- f32 little-endian blobs via PreviewCodec, matching capture_preview rather than SpectrumCodec's
-- quantized i16 -- at this column count the saving would be a few kilobytes and phase in particular
-- has a range no fixed 0.01-step quantization covers.

CREATE TABLE response_trace (
  id INTEGER PRIMARY KEY,
  capture_id INTEGER NOT NULL REFERENCES capture(id),
  kind TEXT NOT NULL,                 -- 'left' | 'right' | 'gain'

  start_hz REAL NOT NULL,
  end_hz REAL NOT NULL,
  peak_db REAL NOT NULL,
  peak_hz REAL NOT NULL,

  bw1_lower_hz REAL NOT NULL,
  bw1_upper_hz REAL NOT NULL,
  bw1_lower_clamped INTEGER NOT NULL,
  bw1_upper_clamped INTEGER NOT NULL,

  bw3_lower_hz REAL NOT NULL,
  bw3_upper_hz REAL NOT NULL,
  bw3_lower_clamped INTEGER NOT NULL,
  bw3_upper_clamped INTEGER NOT NULL,

  freq_hz BLOB NOT NULL,
  mag_min_db BLOB NOT NULL,
  mag_max_db BLOB NOT NULL,
  phase_deg BLOB NOT NULL,
  coherence BLOB NOT NULL);

CREATE UNIQUE INDEX ix_response_trace ON response_trace(capture_id, kind);

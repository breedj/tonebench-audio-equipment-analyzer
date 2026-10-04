-- Small decimated FFT/oscilloscope previews per sweep point, so the THD-vs-frequency screen's
-- small charts have something to show for a stored result, not just its live run. Deliberately a
-- new table rather than columns on `capture`: a capture already carries per-channel data via the
-- `channel` discriminator on `spectrum`/`metric` (see 002), and a preview is the same shape --
-- one row per (capture, channel, kind). 'kind' distinguishes the FFT preview from the scope
-- preview rather than two more tables, since they share an identical (x, min, max) shape.
--
-- Capped at SpectrumPipeline.PreviewMaxColumns (200) points, f32 not the spectrum table's
-- quantized i16 -- at this size the difference is a few hundred bytes per row, not worth a second
-- codec.

CREATE TABLE capture_preview (
  id INTEGER PRIMARY KEY,
  capture_id INTEGER NOT NULL REFERENCES capture(id),
  channel INTEGER NOT NULL,
  kind TEXT NOT NULL,          -- 'fft' | 'scope'
  x BLOB NOT NULL,             -- n x f32 LE: freqHz (fft) or elapsed seconds (scope)
  min BLOB NOT NULL,           -- n x f32 LE
  max BLOB NOT NULL);          -- n x f32 LE
CREATE UNIQUE INDEX ix_capture_preview_capture_channel_kind ON capture_preview(capture_id, channel, kind);

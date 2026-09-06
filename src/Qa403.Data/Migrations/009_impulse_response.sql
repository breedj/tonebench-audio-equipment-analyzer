-- Swept-sine impulse-response results, plus the loopback calibrations they can be corrected against.
--
-- The gated magnitude and phase curves are deliberately NOT here. They are byte-identical in shape
-- to a frequency response's -- the same deconvolution, the same decimation, the same corner readings
-- -- so they go into `response_trace`, which already has the reader, the CSV exporter and the
-- overlay path. Only what is genuinely new gets a table: the impulse response itself, and the
-- harmonic-distortion curves that come out of the stretch of record the frequency-response
-- measurement throws away.
--
-- `ir` is the *gated* impulse response at full resolution, f32 little-endian via PreviewCodec,
-- truncated to a window around the peak rather than the whole gated span. At the finest resolution
-- the gated span is 1.45 s (1.1 MB) of which everything past a few hundred milliseconds is analyzer
-- noise floor, while everything that reads an impulse response -- the chart, the gated magnitude,
-- and later the decay analyses -- lives in the first tens of milliseconds. Capped so one row is
-- bounded at every sample rate.
--
-- `peak_index` is where the linear impulse sits inside that array. Stored rather than re-found on
-- load: the array keeps samples on both sides of the peak (a band-limited impulse rings before
-- itself), and a reader that assumed index 0 would report the pre-ringing as post-ringing.

CREATE TABLE impulse_result (
  id INTEGER PRIMARY KEY,
  capture_id INTEGER NOT NULL REFERENCES capture(id),
  kind TEXT NOT NULL,                  -- 'left' | 'gain'

  sample_rate_hz REAL NOT NULL,
  peak_index INTEGER NOT NULL,
  peak_volts REAL NOT NULL,

  gate_pre_ms REAL NOT NULL,
  gate_post_ms REAL NOT NULL,
  gate_window TEXT NOT NULL,
  resolution_limit_hz REAL NOT NULL,

  reference_mode TEXT NOT NULL,        -- 'None' | 'RightChannel' | 'StoredLoopback'
  reference_name TEXT,
  latency_samples REAL,

  ir_samples INTEGER NOT NULL,
  ir BLOB NOT NULL);

CREATE UNIQUE INDEX ix_impulse_result ON impulse_result(capture_id, kind);

-- One harmonic order's distortion against fundamental frequency, decimated for display.
--
-- `ord`, not `order`: SQLite reserves that word. `upper_valid_hz` is per order and not derivable
-- from the sweep alone by a reader -- the k-th harmonic can only be observed for fundamentals up to
-- the sweep's end over k -- so it is stored rather than recomputed.
CREATE TABLE impulse_harmonic (
  id INTEGER PRIMARY KEY,
  capture_id INTEGER NOT NULL REFERENCES capture(id),
  ord INTEGER NOT NULL,

  upper_valid_hz REAL NOT NULL,
  fundamental_hz BLOB NOT NULL,
  level_dbc BLOB NOT NULL);

CREATE UNIQUE INDEX ix_impulse_harmonic ON impulse_harmonic(capture_id, ord);

-- The analyzer's own impulse response, kept so a measurement can divide it out.
--
-- Deliberately NOT a child of `capture`, and that is the whole point of the shape: a calibration
-- describes the analyzer and its cables, not any one measurement, project or result. Keeping it out
-- of the capture tree keeps it out of both hand-written delete cascades, which is where this schema
-- has twice lost rows.
--
-- `sample_rate_hz` is a hard key -- a calibration taken at another rate describes different filters,
-- and resampling an impulse response is a separate problem. `ir` holds `peak_offset` samples of
-- pre-impulse ringing before the peak, because an anti-alias filter rings on both sides of its
-- impulse and storing only from the peak onward would throw half its response away.
CREATE TABLE impulse_reference (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  taken_utc TEXT NOT NULL,

  sample_rate_hz REAL NOT NULL,
  source_fft_size INTEGER NOT NULL,
  chirp_start_hz REAL NOT NULL,
  chirp_end_hz REAL NOT NULL,

  removed_delay_samples REAL NOT NULL,
  peak_offset INTEGER NOT NULL,
  ir_samples INTEGER NOT NULL,
  ir BLOB NOT NULL,

  device_serial TEXT,
  is_simulated INTEGER NOT NULL);

CREATE UNIQUE INDEX ix_impulse_reference_name ON impulse_reference(name);

-- The analyzer's own impulse response (the loopback calibration), moved here from the measurement
-- database's 009_impulse_response.sql when projects became one file each.
--
-- A calibration describes the analyzer and its cables, not any one measurement, project or result
-- -- the same reason the impedance fixture calibration lives in this file. Kept per project it would
-- have to be re-taken for every new project, and copied into a project file sent to another machine
-- it would silently divide that machine's measurements by this bench's analyzer response. The table
-- shape is unchanged: `sample_rate_hz` is a hard key (a calibration taken at another rate describes
-- different filters), and `ir` holds `peak_offset` samples of pre-impulse ringing before the peak,
-- because an anti-alias filter rings on both sides of its impulse.
--
-- The measurement-side table still exists in every project file (migration 009 is pinned by
-- MigrationRunnerTests) and is simply left empty; nothing reads it any more.
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

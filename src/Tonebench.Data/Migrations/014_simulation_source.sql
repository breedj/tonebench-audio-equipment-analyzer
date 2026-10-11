-- Imported circuit simulations, stored as ordinary measurement results.
--
-- A simulation imported from an LTspice .raw is a `measurement_result` under an FFTSpectrum
-- measurement, marked here, rather than a kind or a table of its own. That is the same argument
-- impedance reusing `response_trace` rests on: everything that already reads an FFT result keeps
-- working untouched -- the overlay colours, the axis-supplying overlay, the min/max band, the CSV
-- router's Spectrum fallback, the report builders and MeasurementSaveService.ApplyAsync. A new
-- kind would need an arm in every one of them, and worse, the 3-overlay cap is counted *per kind*,
-- so simulations would get a separate budget and a user with three of them checked could not add
-- the measurement they were comparing against to the same chart.
--
-- NULL means a row an older build wrote, i.e. a measured one -- the same convention
-- response_trace.reference_db uses. Only 'simulation' is written today.
ALTER TABLE measurement_result ADD COLUMN source TEXT;

-- The samples themselves. `capture.pcm` has been here since 001 and has never held anything: the
-- whole of src reads or writes it in exactly one place, an assertion in MeasurementRepositoryTests
-- that it comes back NULL. It is reused rather than adding a child table of `capture`, which would
-- need its own CaptureCascade entry and would carry the orphaned-rowid trap that migrations 005,
-- 007 and 010 exist to repair; nothing new hangs off `capture`, so that cascade is unchanged.
--
-- Why the samples are stored at all: the window, the FFT size, the level offset, the analysis band
-- and the start time are all re-appliable afterwards against them, with no re-import. That is the
-- point of the feature, and it is also the only option available -- an LTspice .raw is a
-- disposable artifact that the next simulation run overwrites and that LTspice will delete on its
-- own, so a stored path cannot be read back later.
--
-- pcm holds f32 little-endian samples, interleaved in logical channel order, `capture.channels`
-- wide, in the simulation's own volts (or amps). Nothing is baked in: the level offset and the
-- start-time slice are applied at analysis time, which is what keeps them changeable. f32 rather
-- than f64 is measured rather than assumed -- the same run exported by LTspice at single precision
-- (i.e. without `.options numdgt=7`) gives harmonic levels identical to four decimal places, down
-- to -110 dBc.
--
-- pcm_fs is that blob's own sample rate, which is NOT `capture.fs`: `capture.fs` is the rate the
-- `spectrum` row was analysed at, derived from the run's duration and the FFT size, and the two
-- genuinely differ (1 MHz of simulator grid against a ~172 kHz analysis rate on the reference
-- run). NULL means no pcm, which is every measured capture.
ALTER TABLE capture ADD COLUMN pcm_fs REAL;

-- Partial on IS NOT NULL: the history list groups measured rows from simulations, and an index of
-- only the simulations costs nothing at all while a project has none, which is the normal case.
CREATE INDEX ix_measurement_result_source
  ON measurement_result(measurement_type_id, source) WHERE source IS NOT NULL;

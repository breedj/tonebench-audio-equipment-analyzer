-- Group delay and phase delay per display column of a stored response trace, in seconds, as f32
-- little-endian blobs the same shape as phase_deg.
--
-- Both are derived from the phase, but not from the *stored* phase: that is sampled once per
-- display column, and a derivative of it is aliased wherever the columns are finer than the FFT
-- bins. They are computed at full resolution before decimation, like the bandwidth corners, which
-- is why they have to be stored rather than re-derived on load.
--
-- NULL means a row written before this existed. The chart shows no delay curve for those and the
-- Measurements panel a dash, rather than back-filling from phase_deg: a value derived from the
-- decimated phase would not be the same measurement the live screen showed.
ALTER TABLE response_trace ADD COLUMN group_delay_s BLOB;
ALTER TABLE response_trace ADD COLUMN phase_delay_s BLOB;

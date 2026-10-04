-- The level a stored trace's bandwidth corners were measured down from.
--
-- Until now that was always the trace's own peak, so it could be re-derived from peak_db and never
-- needed storing. It no longer can: the corners are referenced to the median of a passband by
-- default, because an output transformer's leakage resonance sits above the audio band and several
-- dB above the passband, which made every -1 dB figure a measurement of the resonance instead of
-- the amplifier.
--
-- Both readings of one trace share a reference by construction, so this is one column rather than
-- one per reading. NULL means a row written before this existed, where the reference *was* the
-- peak — the chart draws no reference line for those rather than inventing one, because a row
-- saved by an older build may also have been measured with a different rule.
ALTER TABLE response_trace ADD COLUMN reference_db REAL;

-- "Peak" or "BandMedian". TEXT for the same reason response_trace.kind is: a mode added later needs
-- no migration.
ALTER TABLE response_trace ADD COLUMN reference_mode TEXT;

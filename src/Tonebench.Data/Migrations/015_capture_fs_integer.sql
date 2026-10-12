-- One-off repair for capture rows whose sample rate was stored as a REAL.
--
-- capture.fs is declared INTEGER, but SQLite's affinity is advisory: a fractional value is stored
-- as it was given and read back as a double. ResultSummaryRowRaw.SampleRateHz is a `long`, so
-- Dapper then refuses to materialise the row -- and ListResultsAsync reads every row of a
-- measurement in one query, so ONE such row takes down the whole History list with "a parameterless
-- default constructor or one matching signature ... is required", which names nothing that is
-- wrong and arrives long after the save that caused it.
--
-- Observed in the wild 2026-10-12: a THD-vs-frequency measurement whose History list answered 500
-- on every load, with a 404 behind it as the stale list kept asking for a result that had since
-- been deleted. The cause was the stepped-simulation sweep import, which stores the analysis rate
-- the periodic-record probe settled on -- deliberately nudged off an integer so each record holds
-- whole cycles of its own tone, and therefore the first fractional rate this column had ever seen.
-- Every measured capture passes 48000/96000/192000 and had never come near it.
--
-- MeasurementRepository.RoundedFs now rounds at every one of the six places that write this column
-- (four of them did not), so no new row can do this. This rounds the rows already written, which no
-- amount of fixed code would.
--
-- Rounding rather than deleting, because the value is a real figure and nothing reads samples back
-- through it: a spectrum import keeps its own pcm_fs (REAL, and deliberately exact, since the
-- stored samples depend on it) and a sweep stores no samples at all.

UPDATE capture
   SET fs = CAST(ROUND(fs) AS INTEGER)
 WHERE typeof(fs) = 'real';

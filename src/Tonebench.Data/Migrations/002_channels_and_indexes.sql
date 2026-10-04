-- §6.1 has no channel discriminator, so a stereo capture (one FFT per channel, same tick)
-- cannot be stored. One capture row now holds both channels' spectrum rows and both channels'
-- metric rows.

ALTER TABLE spectrum ADD COLUMN channel INTEGER NOT NULL DEFAULT 0;
CREATE UNIQUE INDEX ix_spectrum_capture_channel ON spectrum(capture_id, channel);

-- An 'L.thd_percent' name prefix would keep the DDL untouched but break
-- `WHERE name = 'thd_percent'` across results, which is the only reason ix_metric exists.
ALTER TABLE metric ADD COLUMN channel INTEGER NOT NULL DEFAULT 0;

ALTER TABLE capture ADD COLUMN channels INTEGER NOT NULL DEFAULT 2;
CREATE INDEX ix_capture_ts ON capture(ts);

-- The trash. Deleting a measurement or a saved result now marks the row instead of removing it,
-- and TrashRepository.PurgeOlderThanAsync removes it for real once it has sat here longer than the
-- configured retention (trash.retention, 14 days by default). deleted_utc is a TonebenchTimestamp
-- string, which is lexically sortable, so the purge's `deleted_utc < @cutoff` is a plain indexed
-- string compare -- the reason that format exists.
--
-- A trashed measurement_type deliberately leaves its results' deleted_utc NULL. That is the whole
-- model: restoring the measurement brings every result back without touching them, and the history
-- list hides a result when *either* its own row or its type's row is marked. A result marked on its
-- own therefore stays marked when its type is restored -- it was deleted separately, on purpose --
-- which is also what makes "a result can only be restored while its measurement exists" true.
--
-- Nothing new hangs off `capture`, so the hand-written cascade (CaptureCascade) is unchanged and no
-- rowid is freed by a soft delete, which is the orphan trap that migrations 005, 007 and 010 exist
-- to repair.

ALTER TABLE measurement_type   ADD COLUMN deleted_utc TEXT;
ALTER TABLE measurement_result ADD COLUMN deleted_utc TEXT;

-- (project_id, name) has to stop counting trashed rows. Left whole, a trashed "Amp 1 kHz at 1 W"
-- makes creating a new measurement of that name fail with ProjectsEndpoints' 409 "already exists",
-- against a row nobody can see and nothing in the UI can rename.
--
-- The cost is on the way back: trash "Amp", create a new "Amp", and the restore now collides. That
-- is RestoreTypeAsync's TypeRestoreOutcome.NameTaken, which answers a 409 naming the clash rather
-- than letting a raw SqliteException out.
DROP INDEX ix_measurement_type_name;
CREATE UNIQUE INDEX ix_measurement_type_name
  ON measurement_type(project_id, name) WHERE deleted_utc IS NULL;

-- Partial on IS NOT NULL: the listing and the purge both scan exactly that predicate, and an index
-- of only the trashed rows costs nothing at all while the trash is empty, which is the normal case.
CREATE INDEX ix_measurement_type_trashed   ON measurement_type(deleted_utc)   WHERE deleted_utc IS NOT NULL;
CREATE INDEX ix_measurement_result_trashed ON measurement_result(deleted_utc) WHERE deleted_utc IS NOT NULL;

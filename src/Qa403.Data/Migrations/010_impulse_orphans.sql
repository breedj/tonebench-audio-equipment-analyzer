-- The repair that goes with 009, following 005 and 007.
--
-- Foreign keys are off (see SqliteConnectionFactory), so a child of `capture` that a delete path was
-- never taught about is orphaned in silence, and SQLite then hands the freed capture rowid to a
-- later insert whose own child rows collide with the orphans sitting at that id. It surfaces as
-- "UNIQUE constraint failed" on a save that has nothing wrong with it, and it repeats on every save
-- until the orphans are gone.
--
-- Both delete paths were taught about impulse_result and impulse_harmonic in the same change that
-- created them, so on a database that only ever ran released builds this is a no-op. It ships anyway
-- because an intermediate build is a real path, and because 005 and 007 are both here for want of
-- exactly these two lines.

DELETE FROM impulse_result   WHERE capture_id NOT IN (SELECT id FROM capture);
DELETE FROM impulse_harmonic WHERE capture_id NOT IN (SELECT id FROM capture);

-- One-off repair for rows an earlier delete left behind, not a schema change.
--
-- DeleteResultAsync and DeleteTypeAsync each deleted a result's captures without deleting the
-- capture_preview rows hanging off them (004's table was added after both were written). The rows
-- are not merely clutter: SQLite recycles the rowids those deleted captures freed, so the next
-- sweep saved anywhere in the app inserts a capture that lands on a freed id, and its own preview
-- insert then collides with the orphan already sitting at that (capture_id, channel, kind) --
-- "UNIQUE constraint failed: capture_preview.capture_id, capture_preview.channel,
-- capture_preview.kind" on POST /api/sweep/save, naming a sweep that has nothing to do with it and
-- repeating on every save attempt until the orphans are gone. Both delete paths clean up after
-- themselves now; this clears what they already left, which no amount of fixed code would.
--
-- metric and spectrum are swept the same way. Neither has a unique index that a recycled rowid can
-- collide with, so they cause no failure, but they are orphaned by exactly the same delete and
-- would otherwise attach themselves to a future capture that happens to reuse the id -- a stored
-- result reading back someone else's harmonics. Captures whose result is gone go too, for the same
-- reason one level up.
--
-- Foreign keys are off by default in SQLite (see SqliteConnectionFactory), which is why these rows
-- could be written in the first place and why this has to be a DELETE rather than a re-enabled
-- constraint doing the work retroactively.

DELETE FROM capture_preview WHERE capture_id NOT IN (SELECT id FROM capture);
DELETE FROM metric         WHERE capture_id NOT IN (SELECT id FROM capture);
DELETE FROM spectrum       WHERE capture_id NOT IN (SELECT id FROM capture);

DELETE FROM capture
 WHERE measurement_result_id IS NOT NULL
   AND measurement_result_id NOT IN (SELECT id FROM measurement_result);

-- ...and once those captures are gone, their own children are orphans in turn.
DELETE FROM capture_preview WHERE capture_id NOT IN (SELECT id FROM capture);
DELETE FROM metric         WHERE capture_id NOT IN (SELECT id FROM capture);
DELETE FROM spectrum       WHERE capture_id NOT IN (SELECT id FROM capture);

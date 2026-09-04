-- One-off repair for rows an earlier delete left behind. The same bug as 005, one table later, and
-- for the same reason: response_trace arrived with 006, after both delete paths were written, and
-- neither was taught about it.
--
-- DeleteResultAsync and DeleteTypeAsync deleted a result's captures without deleting the
-- response_trace rows hanging off them. Foreign keys are off (see SqliteConnectionFactory), so
-- nothing objected. SQLite then recycles the rowids the deleted captures freed, so the next
-- frequency response saved anywhere in the app inserts a capture that lands on a freed id, and its
-- own three trace inserts collide with the orphans already sitting at that (capture_id, kind) --
-- "UNIQUE constraint failed: response_trace.capture_id, response_trace.kind" on
-- POST /api/frequency-response/save, naming a measurement that has nothing to do with it and
-- repeating on every save attempt until the orphans are gone.
--
-- Observed in the wild with six orphaned captures' worth of traces (capture ids 168 and 268-272
-- against a live MAX(id) of 267), which is exactly the overlap that made every save fail.
--
-- Both delete paths clean up after themselves now; this clears what they already left, which no
-- amount of fixed code would.

DELETE FROM response_trace WHERE capture_id NOT IN (SELECT id FROM capture);

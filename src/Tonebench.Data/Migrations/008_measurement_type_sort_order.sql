-- The sidebar's measurement list is user-ordered (drag handle), not alphabetical. Until now
-- ListTypesAsync sorted by name, so the backfill reproduces exactly that order per project --
-- an existing database opens looking identical to how it looked before this migration ran.
-- (project_id, name) is unique, so the "how many names sort before mine" count has no ties.
--
-- A new type goes to the end of its project's list: CreateTypeAsync inserts MAX(sort_order) + 1.
-- ORDER BY sort_order, id keeps that deterministic even for rows a hand-edited database left
-- sharing a value.

ALTER TABLE measurement_type ADD COLUMN sort_order INTEGER NOT NULL DEFAULT 0;

UPDATE measurement_type SET sort_order = (
  SELECT COUNT(*) FROM measurement_type AS earlier
  WHERE earlier.project_id = measurement_type.project_id
    AND earlier.name < measurement_type.name);

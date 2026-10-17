-- The Screenshot dialog's kept caption became one caption per measurement on 2026-10-17.
--
-- It was a single `title` plus the `titleFor` naming the measurement it was typed for, which did
-- hold the rule it was written for -- a caption is offered again only on its own measurement, never
-- on another's -- but only until a screenshot was taken of a different measurement, which
-- overwrote the one slot and silently forgot the first caption. Joost asked for one per
-- measurement, so the document now carries `captions`, a list of {measurement, title}.
--
-- Two statements, because there are two kinds of existing document and only one has anything to
-- carry. The first lifts a real stored pair into a one-entry list; the second clears the dead
-- fields out of a document whose pair was empty or absent (Save text never ticked, or a build from
-- before `titleFor` existed, where the title was the last one typed anywhere and belongs to no
-- measurement -- exactly the caption the 2026-10-16 fix stopped offering, so it is dropped rather
-- than filed under a name it was never typed for).
--
-- Unlike 003 this cannot delete the row: it also holds the size, aspect ratio, destination and
-- the Add date and Black and white choices, which are the answers the dialog exists to stop asking
-- twice. json_set/json_remove rewrite exactly the fields that moved.
--
-- Idempotent by being a migration, and again by the guards: once `captions` is present neither
-- statement matches.

UPDATE app_setting
SET value = json_remove(
      json_set(
        value,
        '$.captions',
        json_array(
          json_object(
            'measurement', json_extract(value, '$.titleFor'),
            'title', json_extract(value, '$.title')))),
      '$.title', '$.titleFor')
WHERE key = 'screenshot.settings'
  AND json_valid(value)
  AND json_extract(value, '$.captions') IS NULL
  AND json_extract(value, '$.titleFor') IS NOT NULL
  AND trim(json_extract(value, '$.titleFor')) <> ''
  AND json_extract(value, '$.title') IS NOT NULL
  AND trim(json_extract(value, '$.title')) <> '';

UPDATE app_setting
SET value = json_remove(value, '$.title', '$.titleFor')
WHERE key = 'screenshot.settings'
  AND json_valid(value)
  AND json_extract(value, '$.captions') IS NULL;

-- The sound card's "Settle after opening" default dropped from 3000 ms to 2000 on 2026-10-04 (see
-- SoundCardStreamSettle.DefaultMs, where the remark had said "2 s, measured" all along while the
-- constant said 3000). A changed default only reaches an install that has never written the
-- document, and soundcard.settings is written the moment a card is picked -- so every existing
-- bench carries the old figure in its JSON, as a serialized default nobody chose.
--
-- Unlike 003 this cannot delete the row: it also holds the card's identity, its channel offsets,
-- its front-end gains and the three full-scale figures from the calibration, which cannot be
-- recreated. So json_set rewrites exactly the one field, and only where it is exactly 3000 --
-- a bench that typed its own figure (a card needing longer, or one needing none) keeps it, which
-- is the difference between carrying a default forward and resetting someone's setting.
--
-- Idempotent by being a migration, and idempotent again by the 3000 test: a user who types 3000
-- afterwards keeps it.
UPDATE app_setting
SET value = json_set(value, '$.streamStartSettleMs', 2000.0)
WHERE key = 'soundcard.settings'
  AND json_valid(value)
  AND json_extract(value, '$.streamStartSettleMs') = 3000;

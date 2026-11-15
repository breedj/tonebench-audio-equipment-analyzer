-- A sound-card bench naming two physical interfaces becomes a Split, once, on 2026-11-15.
--
-- DeviceKind.SoundCard narrowed to ONE interface that day: duplex, input-only or output-only of a
-- single card. Two cards are what DeviceKind.Split is for, and the reason is mechanical rather than
-- tidiness -- a split opens a stream per card, where the sound card opened one duplex stream across
-- both, and one stream over two clocks accumulates their difference at the ppm rate until it eats
-- the ring (about half an hour at 100 ppm). So this is not a relabelling: the bench measures
-- differently afterwards, and better.
--
-- WITHOUT THIS MIGRATION an existing two-card bench comes up as a sound card holding two cards,
-- which SoundCardDevice.ConnectAsync now refuses outright -- so the one thing standing between
-- such a user and a bench that will not open is this file.
--
-- WHAT COUNTS AS TWO INTERFACES, and why it is not "the two names differ". Every ordinary one-card
-- bench stores two DIFFERENT names: `In 1-2 (MOTU M Series)` and `Out 1-2 (MOTU M Series)` are one
-- interface and one clock. The interface is the parenthetical, so what is compared is each name
-- from its FIRST `(` to the end -- `(MOTU M Series)` against `(MOTU M Series)`, equal, left alone.
-- A name without a `(` is the ASIO convention, which names the interface and nothing else; an ASIO
-- bench is one full-duplex device and stores the SAME name both sides, so requiring a `(` on both
-- sides correctly skips it rather than reading two blank stems as a difference.
--
-- That mirrors SoundCardEnumerator.Pairing's DifferentInterfaces verdict without re-deriving its
-- InterfaceStem in SQL, and the two agree for every document this app can have written, because a
-- stored sound-card bench always had both sides on ONE host API (the duplex stream could not span
-- two, which collapseToBestApi exists to enforce). Under one host API the only way to get two
-- non-empty, differing parentheticals is two cards. The host-API equality is asserted below anyway
-- rather than assumed: a document that somehow holds a cross-API pair is left for
-- ConnectAsync to refuse, with a message naming Split, instead of being rewritten on a guess.
--
-- ORDER IS LOAD BEARING. device.split is written first and device.analyzer last, because every
-- condition here reads device.analyzer still saying SoundCard. That also makes the whole file
-- idempotent for free: once it says Split, nothing matches again.

-- An existing split document keeps its generator rate and takes a card on both halves.
UPDATE app_setting
SET value = json_set(value, '$.inputKind', 'SoundCard', '$.outputKind', 'SoundCard')
WHERE key = 'device.split'
  AND json_valid(value)
  AND EXISTS (
        SELECT 1 FROM app_setting a
        WHERE a.key = 'device.analyzer'
          AND json_valid(a.value)
          AND json_extract(a.value, '$.kind') = 'SoundCard')
  AND EXISTS (
        SELECT 1 FROM app_setting c
        WHERE c.key = 'soundcard.settings'
          AND json_valid(c.value)
          AND trim(coalesce(json_extract(c.value, '$.inputDeviceName'), '')) <> ''
          AND trim(coalesce(json_extract(c.value, '$.outputDeviceName'), '')) <> ''
          AND coalesce(json_extract(c.value, '$.inputHostApi'), '')
            = coalesce(json_extract(c.value, '$.outputHostApi'), '')
          AND instr(json_extract(c.value, '$.inputDeviceName'), '(') > 0
          AND instr(json_extract(c.value, '$.outputDeviceName'), '(') > 0
          AND substr(
                json_extract(c.value, '$.inputDeviceName'),
                instr(json_extract(c.value, '$.inputDeviceName'), '('))
            <> substr(
                json_extract(c.value, '$.outputDeviceName'),
                instr(json_extract(c.value, '$.outputDeviceName'), '(')));

-- A bench that never configured a split needs the document creating. updated_utc is copied from
-- the card settings rather than computed, so the format is whatever this app already writes.
INSERT INTO app_setting (key, value, updated_utc)
SELECT
  'device.split',
  json_object('inputKind', 'SoundCard', 'outputKind', 'SoundCard', 'generatorSampleRateHz', 0.0),
  c.updated_utc
FROM app_setting c
WHERE c.key = 'soundcard.settings'
  AND NOT EXISTS (SELECT 1 FROM app_setting s WHERE s.key = 'device.split')
  AND json_valid(c.value)
  AND EXISTS (
        SELECT 1 FROM app_setting a
        WHERE a.key = 'device.analyzer'
          AND json_valid(a.value)
          AND json_extract(a.value, '$.kind') = 'SoundCard')
  AND trim(coalesce(json_extract(c.value, '$.inputDeviceName'), '')) <> ''
  AND trim(coalesce(json_extract(c.value, '$.outputDeviceName'), '')) <> ''
  AND coalesce(json_extract(c.value, '$.inputHostApi'), '')
    = coalesce(json_extract(c.value, '$.outputHostApi'), '')
  AND instr(json_extract(c.value, '$.inputDeviceName'), '(') > 0
  AND instr(json_extract(c.value, '$.outputDeviceName'), '(') > 0
  AND substr(
        json_extract(c.value, '$.inputDeviceName'),
        instr(json_extract(c.value, '$.inputDeviceName'), '('))
    <> substr(
        json_extract(c.value, '$.outputDeviceName'),
        instr(json_extract(c.value, '$.outputDeviceName'), '('));

-- Last, for the reason above.
UPDATE app_setting
SET value = json_set(value, '$.kind', 'Split')
WHERE key = 'device.analyzer'
  AND json_valid(value)
  AND json_extract(value, '$.kind') = 'SoundCard'
  AND EXISTS (
        SELECT 1 FROM app_setting c
        WHERE c.key = 'soundcard.settings'
          AND json_valid(c.value)
          AND trim(coalesce(json_extract(c.value, '$.inputDeviceName'), '')) <> ''
          AND trim(coalesce(json_extract(c.value, '$.outputDeviceName'), '')) <> ''
          AND coalesce(json_extract(c.value, '$.inputHostApi'), '')
            = coalesce(json_extract(c.value, '$.outputHostApi'), '')
          AND instr(json_extract(c.value, '$.inputDeviceName'), '(') > 0
          AND instr(json_extract(c.value, '$.outputDeviceName'), '(') > 0
          AND substr(
                json_extract(c.value, '$.inputDeviceName'),
                instr(json_extract(c.value, '$.inputDeviceName'), '('))
            <> substr(
                json_extract(c.value, '$.outputDeviceName'),
                instr(json_extract(c.value, '$.outputDeviceName'), '(')));

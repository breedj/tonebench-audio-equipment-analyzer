-- docs/tonebench-stack-spec.md §6.1, verbatim. Superseded pieces (dut, session, sweep,
-- sweep_point) are removed by 003_project_model.sql -- kept here unmodified so the divergence
-- from the spec is a readable diff across migrations rather than an undocumented rewrite.

CREATE TABLE dut (
  id INTEGER PRIMARY KEY, name TEXT NOT NULL, notes TEXT);

CREATE TABLE session (
  id INTEGER PRIMARY KEY, dut_id INTEGER REFERENCES dut(id),
  ts TEXT NOT NULL, serial TEXT, fw_ver TEXT);

CREATE TABLE capture (
  id INTEGER PRIMARY KEY, session_id INTEGER REFERENCES session(id),
  ts TEXT NOT NULL, fs INTEGER NOT NULL, n INTEGER NOT NULL,
  in_range_dbv REAL, out_range_dbv REAL,
  gen_json TEXT,
  pcm BLOB);                          -- always NULL for now; raw PCM retention is out of scope

CREATE TABLE spectrum (
  id INTEGER PRIMARY KEY, capture_id INTEGER REFERENCES capture(id),
  window TEXT NOT NULL, s1 REAL NOT NULL, s2 REAL NOT NULL,
  enbw_hz REAL NOT NULL, bin_hz REAL NOT NULL,
  norm TEXT NOT NULL,                 -- 'amplitude' | 'psd'
  mag_db BLOB NOT NULL);              -- i16, 0.01 dB steps -> 2 bytes/bin

CREATE TABLE metric (
  capture_id INTEGER REFERENCES capture(id),
  name TEXT NOT NULL, value REAL NOT NULL, unit TEXT);
CREATE INDEX ix_metric ON metric(name, capture_id);

CREATE TABLE sweep (
  id INTEGER PRIMARY KEY, session_id INTEGER REFERENCES session(id),
  kind TEXT NOT NULL);                -- 'thd-vs-freq' | 'thd-vs-level' | ...

CREATE TABLE sweep_point (
  sweep_id INTEGER REFERENCES sweep(id),
  x REAL NOT NULL, capture_id INTEGER REFERENCES capture(id));

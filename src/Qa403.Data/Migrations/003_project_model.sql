-- Replaces §6.1's dut/session/sweep/sweep_point with project -> measurement_type ->
-- measurement_result. A project ("GM70 driver stage") holds several configured measurement
-- kinds; the only kind implemented so far is 'FFTSpectrum'. capture now hangs off a result
-- instead of a session, and carries seq_index so a future time-series measurement is just "more
-- capture rows under the same result" -- no schema change needed for that later.

CREATE TABLE project (
  id INTEGER PRIMARY KEY, name TEXT NOT NULL, notes TEXT, created_utc TEXT NOT NULL);
CREATE UNIQUE INDEX ix_project_name ON project(name);

CREATE TABLE measurement_type (
  id INTEGER PRIMARY KEY, project_id INTEGER NOT NULL REFERENCES project(id),
  kind TEXT NOT NULL,                 -- 'FFTSpectrum' today; more kinds later
  name TEXT NOT NULL, notes TEXT,
  params_json TEXT NOT NULL,          -- the configured parameters, re-appliable to the live app
  created_utc TEXT NOT NULL);
CREATE UNIQUE INDEX ix_measurement_type_name ON measurement_type(project_id, name);

CREATE TABLE measurement_result (
  id INTEGER PRIMARY KEY,
  measurement_type_id INTEGER NOT NULL REFERENCES measurement_type(id),
  name TEXT NOT NULL, notes TEXT, ts TEXT NOT NULL,
  params_json TEXT NOT NULL,          -- the parameters this run actually used
  device_name TEXT, serial TEXT, fw_ver TEXT, is_simulated INTEGER NOT NULL);
CREATE INDEX ix_measurement_result_type_ts ON measurement_result(measurement_type_id, ts);

ALTER TABLE capture ADD COLUMN measurement_result_id INTEGER REFERENCES measurement_result(id);
ALTER TABLE capture ADD COLUMN seq_index INTEGER NOT NULL DEFAULT 0;
CREATE INDEX ix_capture_result ON capture(measurement_result_id, seq_index);

-- Superseded by the model above: dut/session are replaced by project/measurement_type, and a
-- sweep becomes a measurement_type kind whose result owns N captures.
DROP TABLE sweep_point;
DROP TABLE sweep;
DROP TABLE session;
DROP TABLE dut;

-- The configuration database's only table: a key/value store of JSON documents.
--
-- Generic rather than a column per setting, because configuration accretes and every new setting
-- would otherwise be a migration. A document per key also keeps a compound setting -- the impedance
-- fixture is a reference resistor, a drive level and two calibration curves -- written and read as
-- one atomic thing, which is what stops a half-updated fixture from ever being applied.
--
-- Lives in tonebench-config.db, never in qa403.db: see TonebenchConfigDatabaseOptions for why the
-- measurement history has to stay portable and this must not travel with it.
CREATE TABLE app_setting (
  key         TEXT PRIMARY KEY,
  value       TEXT NOT NULL,
  updated_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS installations (
    id_hash TEXT PRIMARY KEY,
    first_seen INTEGER NOT NULL,
    last_seen INTEGER NOT NULL,
    app_version TEXT NOT NULL,
    session_id TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_installations_last_seen
    ON installations(last_seen);

CREATE INDEX IF NOT EXISTS idx_installations_app_version
    ON installations(app_version);

CREATE TABLE IF NOT EXISTS daily_activity (
    activity_date TEXT NOT NULL,
    id_hash TEXT NOT NULL,
    PRIMARY KEY (activity_date, id_hash)
);

CREATE INDEX IF NOT EXISTS idx_daily_activity_date
    ON daily_activity(activity_date);

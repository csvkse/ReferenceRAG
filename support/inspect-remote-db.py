import os
import glob
import sqlite3
import json

data_dir = '/var/lib/docker/volumes/referencerag-dev-data/_data'
if not os.path.exists(data_dir):
    data_dir = '/opt/referencerag-dev/support/data-dev'
if not os.path.exists(data_dir):
    data_dir = '/opt/referencerag/data'

print("=== Scanning databases in", data_dir, "===")
for db_file in glob.glob(os.path.join(data_dir, '*.db')):
    print(f"\n--- Database: {os.path.basename(db_file)} ({os.path.getsize(db_file)} bytes) ---")
    try:
        conn = sqlite3.connect(db_file)
        cur = conn.cursor()
        tables = [r[0] for r in cur.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall()]
        print("  Tables:", tables)
        for t in tables:
            try:
                cnt = cur.execute(f"SELECT count(*) FROM {t}").fetchone()[0]
                print(f"    Table {t}: {cnt} rows")
            except Exception as e:
                print(f"    Table {t}: count error ({e})")
    except Exception as ex:
        print(f"  Error reading {db_file}: {ex}")

# Also check kbs subfolder
kbs_dir = os.path.join(data_dir, 'kbs')
if os.path.exists(kbs_dir):
    print("\n=== Scanning kbs folder ===")
    for root, dirs, files in os.walk(kbs_dir):
        for f in files:
            if f.endswith('.db'):
                p = os.path.join(root, f)
                print(f"\n  KB DB: {p} ({os.path.getsize(p)} bytes)")
                try:
                    conn = sqlite3.connect(p)
                    cur = conn.cursor()
                    tables = [r[0] for r in cur.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall()]
                    print("    Tables:", tables)
                    for t in tables:
                        try:
                            cnt = cur.execute(f"SELECT count(*) FROM {t}").fetchone()[0]
                            print(f"      Table {t}: {cnt} rows")
                        except Exception as e:
                            print(f"      Table {t}: count error ({e})")
                except Exception as ex:
                    print(f"    Error: {ex}")

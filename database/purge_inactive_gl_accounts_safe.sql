BEGIN;

WITH delete_candidates AS (
    SELECT a.id
    FROM gl_accounts a
    WHERE a.is_active = FALSE
      AND NOT EXISTS (
          SELECT 1
          FROM gl_accounts c
          WHERE c.parent_account_id = a.id
      )
      AND NOT EXISTS (
          SELECT 1
          FROM gl_journal_details d
          WHERE d.account_id = a.id
      )
      AND NOT EXISTS (
          SELECT 1
          FROM gl_ledger_entries le
          WHERE le.account_id = a.id
      )
)
DELETE FROM gl_accounts a
USING delete_candidates d
WHERE a.id = d.id;

COMMIT;

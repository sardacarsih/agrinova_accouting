BEGIN;

DO $$
DECLARE
    v_company_id BIGINT := 1;
    v_supported_rows INT;
    v_deleted_batch INT;
    v_deleted_total INT := 0;
    v_deactivated_total INT := 0;
    v_remaining_total INT;
BEGIN
    SELECT COUNT(*)
    INTO v_supported_rows
    FROM gl_accounts
    WHERE company_id = v_company_id
      AND is_active = TRUE
      AND substring(account_code from 1 for 2) IN ('20', '80', '81');

    IF v_supported_rows = 0 THEN
        RAISE EXCEPTION 'Workbook COA for company % is not active. Run workbook reseed before legacy cleanup.', v_company_id;
    END IF;

    LOOP
        WITH delete_batch AS (
            SELECT a.id
            FROM gl_accounts a
            WHERE a.company_id = v_company_id
              AND a.account_code LIKE '10.%'
              AND NOT EXISTS (
                  SELECT 1
                  FROM gl_accounts child
                  WHERE child.parent_account_id = a.id
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
        USING delete_batch d
        WHERE a.id = d.id;

        GET DIAGNOSTICS v_deleted_batch = ROW_COUNT;
        v_deleted_total := v_deleted_total + v_deleted_batch;
        EXIT WHEN v_deleted_batch = 0;
    END LOOP;

    UPDATE gl_accounts a
    SET is_active = FALSE,
        updated_by = 'SEED',
        updated_at = NOW()
    WHERE a.company_id = v_company_id
      AND a.account_code LIKE '10.%'
      AND a.is_active = TRUE;

    GET DIAGNOSTICS v_deactivated_total = ROW_COUNT;

    SELECT COUNT(*)
    INTO v_remaining_total
    FROM gl_accounts
    WHERE company_id = v_company_id
      AND account_code LIKE '10.%';

    RAISE NOTICE 'Legacy 10.* cleanup for company % deleted %, deactivated %, remaining % row(s).',
        v_company_id,
        v_deleted_total,
        v_deactivated_total,
        v_remaining_total;
END
$$;

SELECT 'legacy_10_cleanup' AS check_name,
       COUNT(*)::text AS total_legacy_rows,
       COUNT(*) FILTER (WHERE is_active)::text AS active_legacy_rows,
       COUNT(*) FILTER (
           WHERE EXISTS (
               SELECT 1
               FROM gl_journal_details d
               WHERE d.account_id = a.id
           )
           OR EXISTS (
               SELECT 1
               FROM gl_ledger_entries le
               WHERE le.account_id = a.id
           )
       )::text AS referenced_legacy_rows
FROM gl_accounts a
WHERE a.company_id = 1
  AND a.account_code LIKE '10.%';

COMMIT;

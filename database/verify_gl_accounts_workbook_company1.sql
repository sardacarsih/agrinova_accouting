SELECT 'company' AS check_name,
       c.id::text AS value_1,
       c.code AS value_2,
       c.name AS value_3
FROM org_companies c
WHERE c.id = 1;

SELECT 'workbook_account_summary' AS check_name,
       COUNT(*)::text AS total_rows,
       COUNT(*) FILTER (WHERE is_active)::text AS active_rows,
       COUNT(*) FILTER (WHERE upper(account_code) LIKE '20.%')::text AS asset_family_rows
FROM gl_accounts
WHERE company_id = 1
  AND (
      upper(account_code) LIKE '20.%'
      OR upper(account_code) LIKE '80.%'
      OR upper(account_code) LIKE '81.%');

SELECT 'legacy_10_summary' AS check_name,
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
WHERE company_id = 1
  AND upper(account_code) LIKE '10.%';

SELECT 'format_summary' AS check_name,
       COUNT(*) FILTER (WHERE account_code ~ '^[0-9]{2}\.[0-9]{5}\.[0-9]{3}$')::text AS valid_format_rows,
       COUNT(*) FILTER (WHERE account_code !~ '^[0-9]{2}\.[0-9]{5}\.[0-9]{3}$')::text AS invalid_format_rows,
       COUNT(*) FILTER (
           WHERE substring(account_code from 1 for 2) NOT IN ('20', '80', '81')
       )::text AS unsupported_prefix_rows
FROM gl_accounts
WHERE company_id = 1
  AND (
      upper(account_code) LIKE '20.%'
      OR upper(account_code) LIKE '80.%'
      OR upper(account_code) LIKE '81.%');

SELECT 'prefix_type_mismatch' AS check_name,
       COUNT(*)::text AS mismatch_rows,
       COALESCE(MIN(account_code), '') AS sample_min_code,
       COALESCE(MAX(account_code), '') AS sample_max_code
FROM gl_accounts
WHERE company_id = 1
  AND (
      (substring(account_code from 1 for 2) = '20' AND upper(account_type) <> 'ASSET')
      OR (substring(account_code from 1 for 2) IN ('80', '81') AND upper(account_type) <> 'EXPENSE')
  );

WITH RECURSIVE account_tree AS (
    SELECT id,
           company_id,
           1 AS expected_level,
           ARRAY[id] AS visited_ids
    FROM gl_accounts
    WHERE company_id = 1
      AND parent_account_id IS NULL
    UNION ALL
    SELECT c.id,
           c.company_id,
           p.expected_level + 1 AS expected_level,
           p.visited_ids || c.id AS visited_ids
    FROM gl_accounts c
    JOIN account_tree p
      ON p.id = c.parent_account_id
     AND p.company_id = c.company_id
    WHERE c.company_id = 1
      AND NOT (c.id = ANY(p.visited_ids))
)
SELECT 'hierarchy_mismatch' AS check_name,
       COUNT(*)::text AS mismatch_rows,
       COALESCE(MIN(g.account_code), '') AS sample_min_code,
       COALESCE(MAX(g.account_code), '') AS sample_max_code
FROM gl_accounts g
JOIN account_tree t ON t.id = g.id
WHERE g.company_id = 1
  AND g.hierarchy_level IS DISTINCT FROM t.expected_level;

SELECT 'posting_mismatch' AS check_name,
       COUNT(*)::text AS mismatch_rows,
       COALESCE(MIN(g.account_code), '') AS sample_min_code,
       COALESCE(MAX(g.account_code), '') AS sample_max_code
FROM gl_accounts g
WHERE g.company_id = 1
  AND (
      (EXISTS (SELECT 1 FROM gl_accounts c WHERE c.parent_account_id = g.id) AND g.is_posting = TRUE)
      OR
      (NOT EXISTS (SELECT 1 FROM gl_accounts c WHERE c.parent_account_id = g.id) AND g.is_posting = FALSE)
  );

SELECT 'required_nodes' AS check_name,
       account_code AS value_1,
       account_name AS value_2,
       hierarchy_level::text AS value_3
FROM gl_accounts
WHERE company_id = 1
  AND account_code IN (
      '20.00000.000',
      '20.00000.600',
      '20.00000.607',
      '80.00000.000',
      '80.00000.600',
      '80.00000.607',
      '81.00000.000',
      '81.00000.600',
      '81.00000.607')
ORDER BY account_code;

SELECT 'journal_orphans' AS check_name,
       COUNT(*)::text AS missing_account_refs,
       '' AS value_2,
       '' AS value_3
FROM gl_journal_details d
JOIN gl_journal_headers h ON h.id = d.header_id
LEFT JOIN gl_accounts a ON a.id = d.account_id
WHERE h.company_id = 1
  AND a.id IS NULL;

SELECT 'ledger_orphans' AS check_name,
       COUNT(*)::text AS missing_account_refs,
       '' AS value_2,
       '' AS value_3
FROM gl_ledger_entries le
LEFT JOIN gl_accounts a ON a.id = le.account_id
WHERE le.company_id = 1
  AND a.id IS NULL;

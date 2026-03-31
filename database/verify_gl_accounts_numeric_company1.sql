SELECT 'company' AS check_name,
       c.id::text AS value_1,
       c.code AS value_2,
       c.name AS value_3
FROM org_companies c
WHERE c.id = 1;

SELECT 'account_summary' AS check_name,
       COUNT(*)::text AS total_rows,
       COUNT(*) FILTER (WHERE is_active)::text AS active_rows,
       COUNT(*) FILTER (WHERE NOT is_active)::text AS inactive_rows
FROM gl_accounts
WHERE company_id = 1;

SELECT 'format_summary' AS check_name,
       COUNT(*) FILTER (WHERE account_code ~ '^[0-9]{2}\.[0-9]{5}\.[0-9]{3}$')::text AS numeric_format_rows,
       COUNT(*) FILTER (WHERE account_code !~ '^[0-9]{2}\.[0-9]{5}\.[0-9]{3}$')::text AS invalid_format_rows,
       COUNT(*) FILTER (WHERE is_active AND account_code ~ '^[A-Z]{2}\.')::text AS active_legacy_rows
FROM gl_accounts
WHERE company_id = 1;

SELECT 'type_summary' AS check_name,
       COUNT(*) FILTER (WHERE upper(account_type) IN ('ASSET', 'LIABILITY', 'EQUITY', 'REVENUE', 'EXPENSE'))::text AS valid_type_rows,
       COUNT(*) FILTER (WHERE upper(account_type) NOT IN ('ASSET', 'LIABILITY', 'EQUITY', 'REVENUE', 'EXPENSE'))::text AS invalid_type_rows,
       COUNT(*) FILTER (
           WHERE (
               upper(account_type) IN ('ASSET', 'EXPENSE') AND normal_balance <> 'D'
           ) OR (
               upper(account_type) IN ('LIABILITY', 'EQUITY', 'REVENUE') AND normal_balance <> 'C'
           )
       )::text AS invalid_balance_rows
FROM gl_accounts
WHERE company_id = 1;

SELECT 'duplicate_active_codes' AS check_name,
       COALESCE(COUNT(*), 0)::text AS duplicate_code_count,
       COALESCE(string_agg(account_code, ', ' ORDER BY account_code), '') AS duplicate_codes,
       '' AS value_3
FROM (
    SELECT account_code
    FROM gl_accounts
    WHERE company_id = 1
      AND is_active = TRUE
    GROUP BY account_code
    HAVING COUNT(*) > 1
) d;

SELECT 'required_roots' AS check_name,
       account_code AS value_1,
       account_name AS value_2,
       hierarchy_level::text AS value_3
FROM gl_accounts
WHERE company_id = 1
  AND account_code IN (
      '10.00001.000',
      '10.00002.000',
      '10.00003.000',
      '10.00004.000',
      '10.00005.000')
ORDER BY account_code;

SELECT 'cash_bank_nodes' AS check_name,
       account_code AS value_1,
       account_name AS value_2,
       coalesce(report_group, '') AS value_3
FROM gl_accounts
WHERE company_id = 1
  AND account_code IN (
      '10.01101.000',
      '10.01101.001',
      '10.01101.002',
      '10.01101.003')
ORDER BY account_code;

SELECT 'retained_earnings' AS check_name,
       account_code AS value_1,
       account_name AS value_2,
       upper(account_type) AS value_3
FROM gl_accounts
WHERE company_id = 1
  AND account_code IN ('10.00303.000', '10.00303.001')
ORDER BY account_code;

SELECT 'cost_center_required' AS check_name,
       COUNT(*)::text AS value_1,
       MIN(account_code) AS value_2,
       MAX(account_code) AS value_3
FROM gl_accounts
WHERE company_id = 1
  AND is_active = TRUE
  AND requires_cost_center = TRUE;

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

SELECT 'active_code_sample' AS check_name,
       account_code AS value_1,
       account_name AS value_2,
       upper(account_type) AS value_3
FROM gl_accounts
WHERE company_id = 1
  AND is_active = TRUE
ORDER BY account_code
LIMIT 25;

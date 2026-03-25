-- Legacy cleanup helper queries for gl_accounts after hierarchy migration.
-- Read-only analysis queries (no data changes).
--
-- Usage:
--   psql -h 127.0.0.1 -p 5432 -U <user> -d <db> -f database/gl_accounts_cleanup_candidates.sql

\echo '=== 1) Root-level accounts per company (potential legacy flat COA) ==='
SELECT c.code AS company_code,
       a.id,
       a.account_code,
       a.account_name,
       a.account_type,
       a.is_posting,
       a.is_active
FROM gl_accounts a
JOIN org_companies c ON c.id = a.company_id
WHERE a.parent_account_id IS NULL
ORDER BY c.code, a.account_code;

\echo '=== 2) Posting accounts that currently have children (should be zero) ==='
SELECT c.code AS company_code,
       p.id,
       p.account_code,
       p.account_name,
       COUNT(ch.id) AS child_count
FROM gl_accounts p
JOIN gl_accounts ch ON ch.parent_account_id = p.id
JOIN org_companies c ON c.id = p.company_id
WHERE p.is_posting = TRUE
GROUP BY c.code, p.id, p.account_code, p.account_name
ORDER BY c.code, p.account_code;

\echo '=== 3) Accounts with type/normal-balance mismatch (should be zero) ==='
SELECT c.code AS company_code,
       a.id,
       a.account_code,
       a.account_name,
       a.account_type,
       a.normal_balance
FROM gl_accounts a
JOIN org_companies c ON c.id = a.company_id
WHERE (a.account_type IN ('ASSET', 'EXPENSE') AND a.normal_balance <> 'D')
   OR (a.account_type IN ('LIABILITY', 'EQUITY', 'REVENUE') AND a.normal_balance <> 'C')
ORDER BY c.code, a.account_code;

\echo '=== 4) Potential duplicate semantics: same name, same company, different code ==='
SELECT c.code AS company_code,
       UPPER(BTRIM(a.account_name)) AS normalized_name,
       COUNT(*) AS account_count,
       STRING_AGG(a.account_code, ', ' ORDER BY a.account_code) AS codes
FROM gl_accounts a
JOIN org_companies c ON c.id = a.company_id
GROUP BY c.code, UPPER(BTRIM(a.account_name))
HAVING COUNT(*) > 1
ORDER BY c.code, normalized_name;

\echo '=== 5) Journal usage count by account (to avoid deactivating used accounts blindly) ==='
SELECT c.code AS company_code,
       a.account_code,
       a.account_name,
       COUNT(d.id) AS journal_line_count
FROM gl_accounts a
JOIN org_companies c ON c.id = a.company_id
LEFT JOIN gl_journal_details d ON d.account_id = a.id
GROUP BY c.code, a.account_code, a.account_name
ORDER BY c.code, journal_line_count DESC, a.account_code;

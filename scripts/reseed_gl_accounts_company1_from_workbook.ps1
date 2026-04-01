param(
    [string]$WorkbookPath = 'D:\VSCODE\wpf\MASTER AKUN BLOK COST CENTRE RESEED 20 80 81 LVL1.XLSX',
    [string]$CleanupSqlPath = 'D:\VSCODE\wpf\database\cleanup_gl_accounts_legacy_10_company1.sql',
    [string]$VerificationSqlPath = 'D:\VSCODE\wpf\database\verify_gl_accounts_workbook_company1.sql',
    [int]$CompanyId = 1,
    [string]$ConnectionHost = '127.0.0.1',
    [int]$ConnectionPort = 5432,
    [string]$DatabaseName = 'agrinova_accounting',
    [string]$Username = 'agrinova',
    [string]$Password = 'itBOSS'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-EntryText {
    param(
        [System.IO.Compression.ZipArchive]$Zip,
        [string]$Name
    )

    $entry = $Zip.Entries | Where-Object FullName -eq $Name
    if (-not $entry) {
        return $null
    }

    $reader = [System.IO.StreamReader]::new($entry.Open())
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Close()
    }
}

function Quote-SqlText {
    param([string]$Value)

    if ($null -eq $Value) {
        return 'NULL'
    }

    return "'" + $Value.Replace("'", "''").Trim() + "'"
}

$stream = [System.IO.File]::Open($WorkbookPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
$zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read, $false)

try {
    $xml = [System.Xml.XmlDocument]::new()
    $xml.LoadXml((Get-EntryText -Zip $zip -Name 'xl/worksheets/sheet1.xml'))
    $ns = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
    $ns.AddNamespace('d', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')

    $rawRows = @()
    foreach ($row in $xml.SelectNodes('//d:sheetData/d:row[position()>1]', $ns)) {
        $rowNo = [int]$row.GetAttribute('r')
        $values = @{}
        foreach ($col in 'A', 'B', 'D', 'E') {
            $node = $row.SelectSingleNode("d:c[starts-with(@r,'$col')]//d:t", $ns)
            $values[$col] = if ($node) { $node.InnerText } else { '' }
        }

        $account = $values['A'].Trim()
        if (-not $account) {
            continue
        }

        $rawRows += [pscustomobject]@{
            Row       = $rowNo
            Account   = $account.Replace('XXXXX', '00000')
            Name      = $values['B'].Trim()
            Level     = [int]$values['D']
            Parent    = $values['E'].Trim().Replace('XXXXX', '00000')
        }
    }

    $byCode = @{}
    foreach ($row in $rawRows) {
        $byCode[$row.Account] = $row
    }

    $desiredRows = @(
        $byCode.Values |
            Sort-Object Level, Account
    )

    $parentCodes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $desiredRows) {
        if (-not [string]::IsNullOrWhiteSpace($row.Parent)) {
            [void]$parentCodes.Add($row.Parent)
        }
    }

    $valueLines = [System.Collections.Generic.List[string]]::new()
    foreach ($row in $desiredRows) {
        $prefix = ($row.Account -split '\.')[0]
        $accountType = if ($prefix -eq '20') { 'ASSET' } else { 'EXPENSE' }
        $reportGroup = if ($prefix -eq '20') { 'BS_ASSET' } else { 'PL_EXPENSE' }
        $isPosting = -not $parentCodes.Contains($row.Account)
        $requiresCostCenter = $isPosting -and $row.Level -ge 5
        $parentSql = if ([string]::IsNullOrWhiteSpace($row.Parent)) { 'NULL' } else { Quote-SqlText $row.Parent }

        $valueLines.Add(
            "    (" +
            (Quote-SqlText $row.Account) + ', ' +
            $parentSql + ', ' +
            (Quote-SqlText $row.Name) + ', ' +
            $row.Level + ', ' +
            (Quote-SqlText $accountType) + ", 'D', " +
            $row.Row + ', ' +
            (Quote-SqlText $reportGroup) + ', ' +
            ($(if ($requiresCostCenter) { 'TRUE' } else { 'FALSE' })) + ', ' +
            ($(if ($isPosting) { 'TRUE' } else { 'FALSE' })) +
            ')'
        ) | Out-Null
    }

    $desiredCodesSql = ($desiredRows | ForEach-Object { Quote-SqlText $_.Account }) -join ', '
    $sqlPath = Join-Path $env:TEMP ("reseed_gl_accounts_company_${CompanyId}_" + [guid]::NewGuid().ToString('N') + '.sql')
    $sql = @"
BEGIN;

ALTER TABLE gl_accounts
    ADD COLUMN IF NOT EXISTS hierarchy_level INT NOT NULL DEFAULT 1;

CREATE TEMP TABLE tmp_master_akun_seed (
    account_code VARCHAR(80) NOT NULL,
    parent_code VARCHAR(80) NULL,
    account_name VARCHAR(200) NOT NULL,
    hierarchy_level INT NOT NULL,
    account_type VARCHAR(20) NOT NULL,
    normal_balance CHAR(1) NOT NULL,
    sort_order INT NOT NULL,
    report_group VARCHAR(50) NOT NULL,
    requires_cost_center BOOLEAN NOT NULL,
    is_posting BOOLEAN NOT NULL
) ON COMMIT DROP;

INSERT INTO tmp_master_akun_seed (
    account_code,
    parent_code,
    account_name,
    hierarchy_level,
    account_type,
    normal_balance,
    sort_order,
    report_group,
    requires_cost_center,
    is_posting
)
VALUES
$($valueLines -join ",`r`n");

INSERT INTO gl_accounts (
    company_id,
    account_code,
    account_name,
    account_type,
    normal_balance,
    parent_account_id,
    hierarchy_level,
    is_posting,
    is_active,
    sort_order,
    report_group,
    requires_department,
    requires_project,
    requires_cost_center,
    created_by,
    updated_by,
    created_at,
    updated_at
)
SELECT $CompanyId,
       s.account_code,
       s.account_name,
       s.account_type,
       s.normal_balance,
       NULL,
       s.hierarchy_level,
       s.is_posting,
       TRUE,
       s.sort_order,
       s.report_group,
       FALSE,
       FALSE,
       s.requires_cost_center,
       'SEED',
       'SEED',
       NOW(),
       NOW()
FROM tmp_master_akun_seed s
ON CONFLICT (company_id, account_code) DO UPDATE
SET account_name = EXCLUDED.account_name,
    account_type = EXCLUDED.account_type,
    normal_balance = EXCLUDED.normal_balance,
    is_active = TRUE,
    sort_order = EXCLUDED.sort_order,
    report_group = EXCLUDED.report_group,
    requires_department = FALSE,
    requires_project = FALSE,
    requires_cost_center = EXCLUDED.requires_cost_center,
    updated_by = 'SEED',
    updated_at = NOW();

UPDATE gl_accounts a
SET parent_account_id = p.id,
    updated_by = 'SEED',
    updated_at = NOW()
FROM tmp_master_akun_seed s
LEFT JOIN gl_accounts p
  ON p.company_id = $CompanyId
 AND p.account_code = s.parent_code
WHERE a.company_id = $CompanyId
  AND a.account_code = s.account_code
  AND a.parent_account_id IS DISTINCT FROM p.id;

WITH RECURSIVE account_tree AS (
    SELECT a.id,
           1 AS expected_hierarchy_level,
           ARRAY[a.id] AS visited_ids
    FROM gl_accounts a
    WHERE a.company_id = $CompanyId
      AND a.parent_account_id IS NULL
    UNION ALL
    SELECT c.id,
           p.expected_hierarchy_level + 1 AS expected_hierarchy_level,
           p.visited_ids || c.id AS visited_ids
    FROM gl_accounts c
    JOIN account_tree p ON p.id = c.parent_account_id
    WHERE c.company_id = $CompanyId
      AND NOT (c.id = ANY(p.visited_ids))
)
UPDATE gl_accounts a
SET hierarchy_level = t.expected_hierarchy_level,
    is_posting = NOT EXISTS (
        SELECT 1
        FROM gl_accounts child
        WHERE child.parent_account_id = a.id
    ),
    updated_by = 'SEED',
    updated_at = NOW()
FROM account_tree t
WHERE a.id = t.id
  AND (
      a.hierarchy_level IS DISTINCT FROM t.expected_hierarchy_level
      OR a.is_posting IS DISTINCT FROM NOT EXISTS (
          SELECT 1
          FROM gl_accounts child
          WHERE child.parent_account_id = a.id
      )
  );

DELETE FROM gl_accounts a
WHERE a.company_id = $CompanyId
  AND (a.account_code LIKE '20.00000.%' OR a.account_code LIKE '80.00000.%' OR a.account_code LIKE '81.00000.%')
  AND a.account_code NOT IN ($desiredCodesSql)
  AND NOT EXISTS (SELECT 1 FROM gl_accounts c WHERE c.parent_account_id = a.id)
  AND NOT EXISTS (SELECT 1 FROM gl_journal_details d WHERE d.account_id = a.id)
  AND NOT EXISTS (SELECT 1 FROM gl_ledger_entries le WHERE le.account_id = a.id);

UPDATE gl_accounts a
SET account_level = s.hierarchy_level
FROM tmp_master_akun_seed s
WHERE a.company_id = $CompanyId
  AND a.account_code = s.account_code;

COMMIT;
"@

    Set-Content -LiteralPath $sqlPath -Value $sql -Encoding UTF8
    $env:PGPASSWORD = $Password

    & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' `
        -h $ConnectionHost `
        -p $ConnectionPort `
        -U $Username `
        -d $DatabaseName `
        -v ON_ERROR_STOP=1 `
        -f $sqlPath

    if (Test-Path -LiteralPath $CleanupSqlPath) {
        & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' `
            -h $ConnectionHost `
            -p $ConnectionPort `
            -U $Username `
            -d $DatabaseName `
            -v ON_ERROR_STOP=1 `
            -f $CleanupSqlPath
    }

    & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' `
        -h $ConnectionHost `
        -p $ConnectionPort `
        -U $Username `
        -d $DatabaseName `
        -P pager=off `
        -F '|' `
        -R "`n" `
        -At `
        -c "select count(*) from gl_accounts where company_id = $CompanyId and (account_code like '20.00000.%' or account_code like '80.00000.%' or account_code like '81.00000.%');"

    & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' `
        -h $ConnectionHost `
        -p $ConnectionPort `
        -U $Username `
        -d $DatabaseName `
        -P pager=off `
        -F '|' `
        -R "`n" `
        -At `
        -c "select count(distinct account_code) from gl_accounts where company_id = $CompanyId and (account_code like '20.00000.%' or account_code like '80.00000.%' or account_code like '81.00000.%');"

    & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' `
        -h $ConnectionHost `
        -p $ConnectionPort `
        -U $Username `
        -d $DatabaseName `
        -P pager=off `
        -F '|' `
        -R "`n" `
        -At `
        -c "select count(*) from gl_accounts where company_id = $CompanyId and is_active = true and account_code like '10.%';"

    & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' `
        -h $ConnectionHost `
        -p $ConnectionPort `
        -U $Username `
        -d $DatabaseName `
        -P pager=off `
        -F '|' `
        -R "`n" `
        -At `
        -c "select account_code, account_name, hierarchy_level, is_posting from gl_accounts where company_id = $CompanyId and account_code in ('20.00000.000','20.00000.600','20.00000.643','80.00000.000','80.00000.600','80.00000.603','81.00000.000','81.00000.600','81.00000.643','20.00000.607') order by account_code;"

    if (Test-Path -LiteralPath $VerificationSqlPath) {
        & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' `
            -h $ConnectionHost `
            -p $ConnectionPort `
            -U $Username `
            -d $DatabaseName `
            -P pager=off `
            -f $VerificationSqlPath
    }
}
finally {
    $zip.Dispose()
    $stream.Dispose()
}

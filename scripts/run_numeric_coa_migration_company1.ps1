param(
    [string]$AppSettingsPath = "D:\VSCODE\wpf\appsettings.json",
    [string]$MigrationSqlPath = "D:\VSCODE\wpf\database\reseed_gl_accounts_numeric_company.sql",
    [string]$VerificationSqlPath = "D:\VSCODE\wpf\database\verify_gl_accounts_numeric_company1.sql",
    [switch]$VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Parse-ConnectionString {
    param([string]$ConnectionString)

    $result = @{}
    foreach ($segment in ($ConnectionString -split ';')) {
        if ([string]::IsNullOrWhiteSpace($segment)) {
            continue
        }

        $parts = $segment -split '=', 2
        if ($parts.Length -ne 2) {
            continue
        }

        $key = $parts[0].Trim().ToLowerInvariant()
        $value = $parts[1].Trim()
        if ($key.Length -gt 0) {
            $result[$key] = $value
        }
    }

    return $result
}

if (-not (Test-Path -LiteralPath $AppSettingsPath)) {
    throw "App settings file not found: $AppSettingsPath"
}

if (-not (Test-Path -LiteralPath $MigrationSqlPath)) {
    throw "Migration SQL file not found: $MigrationSqlPath"
}

if (-not (Test-Path -LiteralPath $VerificationSqlPath)) {
    throw "Verification SQL file not found: $VerificationSqlPath"
}

$psql = (Get-Command psql -ErrorAction SilentlyContinue).Source
if ([string]::IsNullOrWhiteSpace($psql)) {
    throw "psql executable not found in PATH."
}

$settings = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json
$connectionString = [string]$settings.DatabaseAuth.ConnectionString
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    throw "DatabaseAuth.ConnectionString not found in $AppSettingsPath"
}

$connection = Parse-ConnectionString -ConnectionString $connectionString
$hostName = if ($connection.ContainsKey("host")) { $connection["host"] } else { "127.0.0.1" }
$port = if ($connection.ContainsKey("port")) { $connection["port"] } else { "5432" }
$database = if ($connection.ContainsKey("database")) { $connection["database"] } else { throw "Database is missing in connection string." }
$username = if ($connection.ContainsKey("username")) { $connection["username"] } elseif ($connection.ContainsKey("user id")) { $connection["user id"] } else { throw "Username is missing in connection string." }
$password = if ($connection.ContainsKey("password")) { $connection["password"] } else { "" }

$env:PGPASSWORD = $password
try {
    if (-not $VerifyOnly) {
        & $psql -h $hostName -p $port -U $username -d $database -v ON_ERROR_STOP=1 -f $MigrationSqlPath
        if ($LASTEXITCODE -ne 0) {
            throw "Migration command failed with exit code $LASTEXITCODE."
        }
    }

    & $psql -h $hostName -p $port -U $username -d $database -v ON_ERROR_STOP=1 -f $VerificationSqlPath
    if ($LASTEXITCODE -ne 0) {
        throw "Verification command failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

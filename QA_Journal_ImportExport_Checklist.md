# QA Checklist: Journal Import/Export (Manual)

Tanggal: 2026-03-20
Scope: Import/Export jurnal XLSX (tab Import, tab Export, tombol Export pada tab Input/List)

## Baseline Teknis (Sudah Dieksekusi)

1. Build app utama
   - Command: `dotnet build D:\VSCODE\wpf\Accounting.csproj`
   - Hasil: PASS (0 error, 0 warning)

2. Build integration tests
   - Command: `dotnet build D:\VSCODE\wpf\tools\IntegrationTests\IntegrationTests.csproj`
   - Hasil: PASS (0 error, 0 warning)

3. Run integration tests
   - Command: `dotnet run --project D:\VSCODE\wpf\tools\IntegrationTests\IntegrationTests.csproj`
   - Hasil: 7 PASS, 1 FAIL
   - Fail case: `AccountingPeriod_CloseCreatesClosingEntry` dengan pesan `Kode akun tidak ditemukan/aktif: HO.10000.000.`
   - Catatan: fail ini bukan alur import/export jurnal langsung, tetapi tetap perlu ditindaklanjuti.

## Test Matrix Manual (End-to-End)

| ID | Skenario | Data Uji | Ekspektasi | Status |
|---|---|---|---|---|
| IMP-01 | Import sukses (valid) | File XLSX dengan sheet `Header` + `Detail`, debit=credit, account code valid | Preview valid, `Import Draft` sukses, jurnal tersimpan status DRAFT | PENDING MANUAL |
| IMP-02 | Import gagal format | Sheet `Header` atau `Detail` tidak ada | Muncul pesan gagal format, draft tidak tersimpan | PENDING MANUAL |
| IMP-03 | Import gagal account code kosong | 1 baris detail tanpa `AccountCode` | Baris invalid di preview + pesan validasi | PENDING MANUAL |
| IMP-04 | Import gagal debit/kredit invalid | Baris debit&kredit sama-sama >0 atau keduanya 0 | Baris invalid + pesan `Debit/Kredit tidak valid.` | PENDING MANUAL |
| IMP-05 | Import gagal tidak seimbang | Total debit != total kredit | Import ditolak dengan pesan tidak seimbang | PENDING MANUAL |
| IMP-06 | Import gagal COA mismatch | Account code tidak ada di COA aktif | Preview invalid setelah validasi COA, commit ditolak | PENDING MANUAL |
| EXP-01 | Export jurnal aktif | Jurnal dibuka di tab Input | File XLSX terbentuk, header/detail sesuai data layar | PENDING MANUAL |
| EXP-02 | Export jurnal terpilih dari daftar | Pilih 1 jurnal di daftar | File XLSX terbentuk sesuai jurnal terpilih | PENDING MANUAL |
| RT-01 | Round-trip export->import | Export file valid lalu import kembali | Data tetap konsisten, journal line valid, total balance | PENDING MANUAL |

## Referensi Implementasi

- Import UI: `Views/Components/JournalImportTabControl.xaml`
- Export UI: `Views/Components/JournalExportTabControl.xaml`
- Command/flow: `ViewModels/JournalManagementViewModel.cs`
- XLSX parser/writer: `Services/JournalXlsxService.cs`

## Catatan Temuan Awal

1. Belum ada integration test khusus untuk alur `JournalXlsxService.Import/Export` end-to-end.
2. Ada 1 integration test fail terkait periode closing entry account mapping (`HO.10000.000`) yang berpotensi memengaruhi stabilitas proses period-end.

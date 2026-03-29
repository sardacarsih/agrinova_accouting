# Panduan Internal: Penggunaan Text dan Colour yang Selaras dengan Skins

## Tujuan

Panduan ini membantu tim UI WPF memilih typography dan warna berbasis token semantik, bukan berdasarkan nama skin tertentu atau hex manual. Dengan pendekatan ini, tampilan tetap konsisten saat pengguna berpindah antara curated DevExpress skins yang tersedia, termasuk varian Win11, Win10, Office 2019, dan VS2019 pada mode terang maupun gelap.

Prinsip utamanya sederhana:

- desain terhadap token `Color.*` dan `Brush.*`
- gunakan style global untuk typography dan state umum
- hindari mengikat keputusan visual ke nama skin tertentu
- batasi hardcoded color/font hanya untuk area branding yang memang sengaja tidak ikut skin

## Arsitektur Tema Saat Ini

Pemilihan skin di aplikasi mengikuti alur berikut:

1. Pengguna memilih skin dari daftar curated themes pada selector.
2. DevExpress menerapkan theme aktif ke aplikasi.
3. `DevExpressThemeResourceBridge` memetakan palette theme itu ke token netral seperti `Color.TextPrimary`, `Brush.Surface`, dan `Brush.Primary`.
4. Komponen XAML memakai token tersebut melalui `DynamicResource`, sehingga warna mengikuti skin tanpa perlu logika per-skin.

Implikasinya:

- jangan menulis aturan seperti "jika Office2019Dark maka pakai warna X"
- jangan memakai hex langsung untuk kebutuhan UI fungsional
- semua komponen baru sebaiknya cukup memakai token semantik yang sudah tersedia

## Aturan Pemakaian Text

Gunakan warna teks berdasarkan peran informasinya, bukan sekadar preferensi visual.

| Token | Gunakan untuk | Hindari untuk |
| --- | --- | --- |
| `Brush.TextPrimary` | judul, isi utama, nilai penting, teks terpilih, konten yang wajib terbaca | hint ringan, metadata sekunder |
| `Brush.TextSecondary` | label field, subjudul, kolom tabel, deskripsi tingkat dua | judul utama, CTA |
| `Brush.TextMuted` | hint, helper text, placeholder, metadata non-kritis, summary kecil | informasi wajib, angka penting, error atau success |
| `Brush.Error` | pesan validasi, status gagal, angka selisih yang bermasalah | CTA umum atau highlight biasa |
| `Brush.Warning` | peringatan, anomali, kondisi yang perlu perhatian | tombol utama, teks biasa |
| `Brush.Success` | status berhasil, indikator sehat, angka positif yang memang bermakna status | CTA umum, judul section |
| `Brush.Info` | badge informasional dan metadata informatif | pengganti `Primary` untuk aksi utama |

Aturan cepat:

- Jika pengguna harus membaca teks itu terlebih dahulu, mulai dari `TextPrimary`.
- Jika teks hanya membantu memahami konteks, turunkan ke `TextSecondary`.
- Jika teks boleh dibaca belakangan, baru gunakan `TextMuted`.
- Warna status hanya dipakai jika teks benar-benar menyatakan status.

## Aturan Pemakaian Surface dan Border

Gunakan layer permukaan sesuai hirarki visual:

| Token | Gunakan untuk |
| --- | --- |
| `Brush.Background` | kanvas utama window atau workspace |
| `Brush.Surface` | card, form, panel utama, popup, dialog |
| `Brush.SurfaceMuted` | header card, section info ringan, panel utilitas, area statis yang perlu sedikit terpisah |
| `Brush.SurfaceAlt` | hover halus, row alternate, variasi layer ringan |
| `Brush.Border` | border default |
| `Brush.BorderSoft` | separator lembut atau gridline |
| `Brush.BorderStrong` | hover border atau penegasan ringan |
| `Brush.Selection` | selected row/item background |
| `Brush.SelectionBorder` | border selected state |
| `Brush.Focus` | focus ring, active border, focus-adjacent highlight |

Aturan cepat:

- Jangan pakai `PrimarySubtle` sebagai pengganti surface biasa.
- Jangan isi panel form utama dengan warna status.
- Untuk state error atau warning pada panel, padukan warna utama dengan pasangan subtle-nya.

## Aturan Pemakaian Accent

Accent utama aplikasi adalah `Primary`.

Gunakan:

- `Brush.Primary` untuk CTA utama, link, tab aktif, active indicator, icon aksen, dan selected emphasis utama
- `Brush.PrimaryLight` untuk hover state tombol primary
- `Brush.PrimaryDark` untuk pressed state tombol primary
- `Brush.PrimarySubtle` untuk selected card ringan, badge background, hover/selection halus, atau panel aksen ringan

Jangan gunakan:

- `Warning`, `Success`, atau `Error` sebagai pengganti CTA utama
- `Primary` untuk semua teks hanya agar terlihat menarik
- `PrimarySubtle` pada area luas yang seharusnya netral

## Typography Standar

Gunakan token typography yang sudah tersedia agar ritme visual tetap konsisten.

| Token/Style | Gunakan untuk |
| --- | --- |
| `FontFamily.Primary` | font default seluruh UI fungsional |
| `Font.Title` / `TitleTextStyle` | heading utama halaman atau card besar |
| `Font.Section` / `SectionHeaderTextStyle` | heading section dan sub-area utama |
| `Font.Body` / `BodyTextStyle` | isi teks normal |
| `Font.Caption` / `FieldLabelTextStyle` | label field |
| `Font.Caption` / `MetaCaptionTextStyle` | hint, meta, caption kecil |

Aturan cepat:

- Utamakan style global dibanding mengatur `FontSize`, `Foreground`, dan `FontWeight` satu per satu.
- Gunakan `FontFamily.Primary` untuk UI biasa.
- Font khusus seperti `Bahnschrift` hanya dipakai jika elemen tersebut bagian dari branding yang disengaja.

## Pengecualian yang Diizinkan

Hardcoded color atau hardcoded font masih boleh dipakai, tetapi hanya pada area branding yang memang dirancang sebagai visual island dan tidak diharapkan ikut berubah mengikuti skin.

Contoh yang masih dapat diterima:

- hero panel atau marketing panel pada login
- ilustrasi brand
- lockup logo, headline brand, atau badge dekoratif yang memang identitas visual tetap

Syarat pengecualian:

- dibatasi pada area branding, bukan pada area kerja utama
- tidak dipakai ulang untuk form, grid, dialog, tab, atau card operasional
- tidak menurunkan keterbacaan pada skin gelap atau terang
- bila ragu, kembali ke token semantik

## Contoh Implementasi

Contoh yang dianjurkan:

```xaml
<StackPanel>
    <TextBlock Text="Username" Style="{StaticResource FieldLabelTextStyle}" />
    <TextBox Margin="0,8,0,0"
             Style="{StaticResource InputTextBoxStyle}" />
    <TextBlock Margin="0,6,0,0"
               Text="Gunakan username perusahaan Anda."
               Style="{StaticResource MetaCaptionTextStyle}" />
</StackPanel>

<Border Margin="0,12,0,0"
        Padding="12,10"
        Background="{DynamicResource Brush.ErrorSubtle}"
        BorderBrush="{DynamicResource Brush.Error}"
        BorderThickness="1">
    <TextBlock Text="Username wajib diisi."
               Foreground="{DynamicResource Brush.Error}" />
</Border>
```

Contoh yang sebaiknya dihindari:

```xaml
<TextBlock Foreground="#6B7280" FontSize="13" Text="Helper text" />
<Border Background="#FFFDEDEE" BorderBrush="#FFB91C1C" BorderThickness="1" />
<Button Foreground="White" Background="#1D4ED8" Content="Save" />
```

Masalah pada contoh di atas:

- warna tidak ikut skin aktif
- ukuran teks dan warna menyimpang dari token yang sudah ada
- `Info` dipakai sebagai CTA umum, padahal seharusnya `Primary`

## Checklist Implementasi XAML Baru

Gunakan checklist ini saat membuat atau me-review komponen baru.

- Semua `Foreground`, `Background`, dan `BorderBrush` memakai `DynamicResource Brush.*`, bukan hex langsung.
- Semua ukuran teks dan font family memakai token atau style global, kecuali area branding yang disengaja.
- `TextPrimary`, `TextSecondary`, dan `TextMuted` dipakai sesuai hirarki informasi.
- Teks penting tidak memakai `TextMuted`.
- Status error memakai `Error` dan, bila berupa panel, dipasangkan dengan `ErrorSubtle`.
- Status warning memakai `Warning` dan, bila berupa panel, dipasangkan dengan `WarningSubtle`.
- Status success memakai `Success`; jangan dipakai sebagai warna default CTA.
- CTA utama dan link memakai `Primary`, bukan warna status.
- Hover, selected, focus, dan disabled mengikuti resource yang sudah tersedia di style global.
- Surface utama memakai `Background` atau `Surface`, bukan warna aksen.
- Jika perlu exception visual, alasan exception ditulis singkat pada PR atau catatan perubahan.
- Komponen baru diuji minimal pada satu skin terang dan satu skin gelap dari daftar curated.

## Skenario Review dan Validasi

Saat melakukan review UI, cek skenario berikut:

### 1. Form Baru

Periksa:

- label field memakai `FieldLabelTextStyle` atau `TextSecondary`
- input memakai style global seperti `InputTextBoxStyle`
- helper text memakai `MetaCaptionTextStyle` atau `TextMuted`
- error message memakai `Error` dan, jika dibungkus panel, `ErrorSubtle`
- tombol primary dan secondary mengikuti style global

### 2. Layar Data-Heavy

Periksa:

- card utama memakai `Surface`
- row selected memakai `Selection`
- tab aktif memakai `Primary`
- angka utama tetap memakai `TextPrimary` kecuali benar-benar status
- border dan gridline memakai token border, bukan abu-abu hardcoded

### 3. Dark Skin

Periksa:

- `TextMuted` tetap terbaca dan tidak terlalu tenggelam
- `PrimarySubtle` masih cukup terlihat sebagai state ringan
- warna status tidak terlalu menyala atau terlalu pudar
- surface bertingkat masih jelas terpisah

### 4. Branding Area

Periksa:

- hardcoded color/font hanya berada di island branding
- style branding tidak bocor ke form, popup, grid, atau dialog kerja
- teks brand tetap terbaca di ukuran window berbeda

## Referensi Implementasi di Repo

Gunakan file berikut sebagai sumber kebenaran saat menambah atau menyesuaikan UI:

- [`Resources/ThemeAliases.xaml`](../Resources/ThemeAliases.xaml): definisi token `Color.*` dan `Brush.*`
- [`Resources/DesignTokens.xaml`](../Resources/DesignTokens.xaml): typography, spacing, radius, dan sizing dasar
- [`Resources/Styles.xaml`](../Resources/Styles.xaml): style dasar untuk window, button, input, grid, tab, dan state umum
- [`Resources/Styles/TextBlockStyles.xaml`](../Resources/Styles/TextBlockStyles.xaml): style text tambahan untuk header, footer, dan metadata
- [`Services/DevExpressThemeResourceBridge.cs`](../Services/DevExpressThemeResourceBridge.cs): bridge yang memetakan skin DevExpress ke token semantik
- [`Views/Components/ThemeSkinsSelectorControl.xaml.cs`](../Views/Components/ThemeSkinsSelectorControl.xaml.cs): daftar curated skins yang ditampilkan ke pengguna

## Ringkasan Keputusan

Untuk UI fungsional:

- token semantik lebih penting daripada nama skin
- style global lebih penting daripada angka/warna manual
- branding boleh berbeda, tetapi harus tetap terisolasi

Jika ada keraguan saat memilih warna atau typography, gunakan opsi yang paling netral dan paling dekat dengan token yang sudah ada. Konsistensi lintas skin lebih penting daripada variasi visual lokal.

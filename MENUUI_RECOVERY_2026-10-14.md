# MenuUi.cs kaybı ve kurtarma — 2026-10-14

## Ne oldu

| Saat | Olay |
|---|---|
| 2026-10-14 00:05 | Son **başarılı** derleme: `bin/Release/MSCSteamMP.dll` |
| 2026-10-14 00:16 | `src/MSCSteamMP/Net/MenuUi.cs` **boşaltıldı** — dosya 5 bayt (BOM + CRLF) |

Sonuç: `MenuUi` tipinin gövdesinin tamamı (248 üyeden 212'si) kayboldu ve mod
**derlenemez hâle geldi**: 208 `error CS`. `MenuUiSkins.cs` (partial class'ın
diğer yarısı) sağlamdı, ama `styleHint`, `Dim`, `Accent`, `Label`, `Fill`,
`Frame`, `Row`, `ShowMain`, `SetStatus`, `Target`, `View`, `IsVisible`,
`TypedCode` … gibi üyeleri tanımsız kaldığı için çağıran dosyalar da
(`MenuController`, `MenuMouse`, `SmpCommand`, `InvitePanel`) patlıyordu.

Proje **sürüm kontrolünde değil** (`.git` yok) ve `.cs~` / `.bak` / editör
yerel geçmişi (VS Code History, Notepad++ backup) kaydı yoktu. Yani dosyanın
kaynağı hiçbir yerde durmuyordu.

## Nasıl kurtarıldı

1. **Son sağlam DLL decompile edildi.** `bin/Release/MSCSteamMP.dll` (00:05)
   hâlâ yerindeydi; `ilspycmd -t SteamMP.MenuUi` ile tipin tamamı çıkarıldı
   (248 üye). Bu, kaynak metni değil C#'a geri çevrilmiş IL'dir: kod doğru,
   **özgün yorumlar kayıp**.
2. **Arşiv bulundu.** `D:\modmscMP\MSCSteamMP.7z` (2026-10-09) içinde
   `src\MSCSteamMP\Net\MenuUi.cs` **74714 bayt** olarak duruyordu — yorumlarıyla
   orijinal kaynak.
3. **Delta çıkarıldı.** Arşiv sürümü ile 14 Ekim DLL'i karşılaştırıldı: arşivde
   üçüncü bir menü satırı (**NEW GAME** + `OnNewGame` olayı) vardı, 14 Ekim
   sürümünde **yoktu** (`MenuTheme.ROW_COUNT = 2` bunu doğrular). Kaybolan
   dosya 2 satırlı sürümdü.
4. **Arşiv dosyası + delta** kuruldu: `RowNewGame`/`OnNewGame` kaldırıldı,
   satırlar yeniden numaralandı (0 = PLAY ONLINE, 1 = GÖRÜNÜM), `OnRow()`
   aynı numaralara göre dallanıyor, `DrawMain` içindeki fazladan `hover = -1;`
   (09 Ekim'den kalmaydı) silindi.

## Doğrulama

* `bin\build.cmd` → **0 hata**.
* `tests\verify.cmd` → 6/6 probe, **649 kontrol ok, 0 hata**,
  `VERIFICATION PASSED`. (PatchProbe: 13/13 Harmony yaması yerinde.)
* **Birebir kanıt:** geri getirilen kaynakla derlenen DLL'in `MenuUi` tipi
  decompile edilip 00:05 DLL'ininkiyle `diff` edildi → **FARK YOK**.
  Yani restore edilen kaynak, kaybolan sürümle aynı IL'i üretiyor; davranış
  değil, sadece yorumlar arşivden geliyor.

## Referans dosyalar (`bin/decomp/`)

| Dosya | İçerik |
|---|---|
| `SteamMP.MenuUi.decompiled.cs` | 00:05 DLL'indeki tipin decompile çıktısı — kurtarmanın ölçütü (oracle) |
| `MenuUi.recovered-from-dll.cs` | Yalnızca DLL'den üretilmiş alternatif kurtarma (yorum yok). Arşiv tabanlı dosya bir sorun çıkarırsa yedek budur. |

## Aynı turda yapılan senkronizasyon düzeltmeleri

1. **`Player/RemotePlayerSync.cs` — null gövde NRE.** `EnsureBody()` sözleşmesi
   "asset yüklenmediyse NULL döner" diyor, ama çağıran dönüşü kontrol etmeden
   `body.ApplyPose(...)` çağırıyordu: asset hazır olana kadar **gelen her konum
   paketi (20/sn)** NullReferenceException atıyor, `BadPackets` şişiyor ve
   `ReceivedPlayerPositions` zaten artmış olduğu için teslim teşhisi
   ("aldım" / "uyguladım") birbirini yalanlıyordu. Artık NULL dönüş sessizce
   atlanıyor (hata değil) ve sonraki paket yeniden deniyor.
2. **`POS_BYTES` 32 → 40.** Paket, baş pitch/yaw alanları eklendiğinde 10 float
   (40 bayt) olmuştu ama sabit 32 kalmıştı; üstelik `WireProbe` kendi elle
   yazdığı 32 baytlık paketi bu sabitle karşılaştırdığı için hatayı
   **göremiyordu**. Sabit düzeltildi, prob artık göndericinin alan kümesini
   yazıp okuyor, ve `OnMessage`'a `AssertBytesRemaining(r, POS_BYTES, ...)`
   kapısı eklendi — sabit üretimde de okunuyor, kayma sessiz kalmıyor.

## Açık konular

1. **NEW GAME satırı:** arşivde vardı, 14 Ekim sürümünde yok. Bilinçli bir
   kaldırma mıydı, yarım kalmış bir düzenleme mi? Geri istenirse satırlar ve
   `MenuTheme.ROW_COUNT` (2 → 3) birlikte değişmeli.
2. **`Net/Protocol.cs` (~373-380) bayat yorum:** `SaveGameChunk` (60) /
   `SaveGameComplete` (61) için "bu kimlikleri kullanan kod KALDIRILDI" ve
   "DOSYA AKTARIMI YOK" diyor. Oysa `Net/SaveTransfer.cs` **canlı**: `build.rsp`
   içinde, `SteamMPBehaviour` içinde `Guard("SaveTransfer", SaveTransfer.Tick)`
   ile her kare çağrılıyor, `OnChunk`/`OnComplete` işleyicileri bağlı, `smp save`
   komutları ve `WireProbe` onu test ediyor. Aynı iddia
   `AuthorityManager`'ın başında da var. Hangisi doğru, netleşmeli.
3. **`RemotePlayerSync.SILENCE_TIMEOUT`** tanımlı ama hiçbir yerde
   kullanılmıyor ("peer sessizse gövde donmasın" davranışı bağlanmamış).
4. **Sürüm kontrolü yok.** Bu olayın tekrarlanmaması için `git init` + düzenli
   arşiv şart; tek yedek 09 Ekim tarihli `.7z` idi ve 5 günlük değişiklikleri
   kapsamıyordu.

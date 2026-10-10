# Warden — İlişki Tabanlı Yetkilendirme Servisi (Zanzibar tarzı)

[English](README.md) · **Türkçe**

"**Bu kullanıcı**, **şu nesne** üzerinde **bunu** yapabilir mi?" sorusunu, sabit rol kontrolleri yerine bir ilişki grafiğinden (klasördeki belgeler, klasördeki klasörler, iç içe gruplardaki kullanıcılar) cevaplar.
Google Zanzibar / SpiceDB tarzında, .NET 10 ve PostgreSQL üzerinde küçük bir yetkilendirme servisi.

## Neler yapıyor

| Alan | Yaklaşım | Kanıt |
|---|---|---|
| **Şema dili** | Tanımlar, ilişkiler (`user \| group#member \| user:*`) ve `+` (birleşim), `&` (kesişim), `-` (dışlama), `->` (ilişki üzerinden ok) ile kurulan izinler için elle yazılmış sözcük çözümleyici (lexer) ve özyinelemeli inişli ayrıştırıcı | Öncelik, yorumlar, sözdizimi hatalarının tam satır/sütunu ve birlikte bildirilen çok sayıda anlamsal hata (bilinmeyen tip, hatalı ok, izin döngüsü, yinelenen ad …) için testler |
| **Check** | İzin ifadesinin ilişki grafiği üzerinde özyinelemeli, paralel değerlendirilmesi; döngü kesme; sessiz ret değil **açık hata** olan derinlik sınırı; yalnızca güvenli olduğunda uygulanan sonuç önbelleği (memoization) | Checker'dan bağımsız yazılmış, aşağıdan yukarıya sabit nokta (fixpoint) **oracle**: 300+ rastgele grafikte (birleşim şeması için döngülü veri, dışlama ve kesişim için döngüsüz veri) her kullanıcı × nesne × izin üzerinde uyuşmalı |
| **Tutarlılık** | Her yazma bir revizyon belirteci (token) döner; okumalar `minimize_latency`, `at_least_as_fresh(token)` veya `fully_consistent` isteyebilir. Okumalar bir revizyondaki anlık görüntüdür | Test: başka bir düğümde yapılan yazma, önbelleğe alınmış başı sunan düğümde belirteç sunulana kadar görünmez; erişimin geri alınması taze okumada anında görünür |
| **Depolama** | Tam revizyon geçmişiyle PostgreSQL (`created_rev` / `deleted_rev`), canlı kayıtlarda kısmi benzersiz indeks, revizyonların sırayla görünür olması için tek ve sıralı yazıcı | Testler: idempotent touch, silme / yeniden ekleme geçmişi, bir yazma içindeki sıra, tek revizyonda 5.000 ilişki, 40 eşzamanlı yazıcı → revizyonlar 1..40 ve her birinde tutarlı anlık görüntü |
| **Toplu okuma** | Bir nesnenin tüm ilişkileri tek sorguda okunur, check oturumu içinde paylaşılır (ve revizyona göre önbelleğe alınır) | Ölçüm: check başına gidiş-dönüş 34'ten 12,8'e, PostgreSQL verimi 5,2 kat (aşağıya bakın) |
| **Ayrıca** | `Expand` (bu izne kim neden sahip), `ListObjects`, ilişki süzgeçleri, değişiklik akışı, anahtar başına kapsamlar (`read`/`write`/`admin`) | API testleri |
| **Güvenli varsayılanlar** | Servis API anahtarı olmadan başlamaz; bilinmeyen izin adı 400, çok derin grafik 422 | Testler |

### Örnek şema

[examples/docs.schema](examples/docs.schema) dosyasına bakın (klasör hiyerarşisi, iç içe gruplar, joker `user:*`, yasaklı kullanıcı için dışlama).

## Hızlı başlangıç

```bash
docker compose up -d --build      # PostgreSQL + 2 servis düğümü (5600, 5601), yalnızca geliştirme API anahtarları
A='authorization: Bearer dev-admin-key-change-me-0001'
curl -X POST localhost:5600/v1/schema -H "$A" -H 'content-type: application/json' -d "$(jq -n --rawfile d examples/docs.schema '{definition:$d}')"

curl -X POST localhost:5600/v1/relationships/write -H "$A" -H 'content-type: application/json' -d '{"updates":[
  {"op":"touch","object":"group:eng","relation":"member","subject":"user:ayse"},
  {"op":"touch","object":"folder:root","relation":"owner","subject":"user:mehmet"},
  {"op":"touch","object":"document:spec","relation":"parent","subject":"folder:root"},
  {"op":"touch","object":"document:spec","relation":"viewer","subject":"group:eng#member"}]}'
# -> {"revision":1,"token":"r1"}

curl -X POST localhost:5601/v1/check -H "$A" -H 'content-type: application/json' \
  -d '{"object":"document:spec","permission":"view","subject":"user:ayse","consistency":{"mode":"at_least_as_fresh","token":"r1"}}'
# -> {"allowed":true,"revision":1,"token":"r1"}   (öteki düğümde soruldu)
```

| Uç nokta | Kapsam | Amaç |
|---|---|---|
| `POST /v1/schema`, `GET /v1/schema` | admin / read | Şema yayınla (doğrulanır, sürümlenir) ve etkin olanı oku |
| `POST /v1/relationships/write` | write | İlişkileri atomik olarak ekle/sil; her güncelleme şemaya göre doğrulanır; belirteci döner |
| `POST /v1/relationships/read` | read | Bir tutarlılık düzeyinde süzgeçli okuma |
| `POST /v1/check` | read | Tek bir izin kontrolü |
| `POST /v1/expand` | read | Bir iznin arkasındaki özne ağacı |
| `POST /v1/lookup/objects` | read | Öznenin kullanabildiği tipteki nesneler |
| `GET /v1/changes?after=` | read | Bir revizyondan sonraki sıralı ekleme ve silmeler |

## Ölçüm

AMD Ryzen 7 9800X3D, Windows, **aynı makinede Docker içinde varsayılan ayarlı** PostgreSQL 17, 887 bin ilişki (30 bin kullanıcı, iç içe gruplu 3 bin grup, 8 derinliğe kadar ağaçlarda 30 bin klasör, 400 bin belge).
İş yükü, rastgele belge × kullanıcı çiftleri için 64 eşzamanlı check'tir; bu **pahalı durumdur**: yalnızca %0,7'si izinli olduğundan bir ret her dalı gezmek zorundadır.

| Depo | Check/sn | Gecikme p50 / p95 / p99 | Check başına okuma |
|---|---|---|---|
| Bellekte (yalnızca algoritma) | 74.000 | 0,06 / 1,0 / 2,5 ms | 12,8 |
| PostgreSQL, önbellek yok | 1.604 | 38,6 / 73 / 87 ms | 12,8 |
| PostgreSQL + revizyon başına önbellek (sunulan hâl) | 19.053 | 2,5 / 10,9 / 16,6 ms | 12,8 (%92,7 isabet) |

Bir nesneyi tek sorguda okumadan önce aynı iş yükü check başına 34 okuma gerektiriyordu ve PostgreSQL'de önbelleksiz/önbellekli 310 / 4.279 check/sn veriyordu.
887 bin ilişkinin toplu yüklenmesi 13 sn sürdü (≈67 bin ilişki/sn, 10.000'lik gruplarla).

**Nasıl okunmalı:** istemci, servis mantığı ve veritabanı tek CPU'yu paylaşıyor, rastgele iş yükünün tekrarlanan sıcak nesneleri için önbellek ısınmış durumda ve veri kümesi sentetik. Bu sayılar maliyetin nerede olduğunu gösterir
(algoritma değil, gidiş-dönüşler); üretimde bir kümenin ne yapacağını değil. Tek düğümde ölçüldü; çok düğümlü ölçekleme ölçülmedi.

## Testler

```bash
dotnet test --solution Warden.slnx      # Docker gerekir (Testcontainers ile gerçek PostgreSQL 17)
```

57 test: ayrıştırıcı ve doğrulayıcı (konumlar dahil), küçük grafiklerde checker (hiyerarşiler, iç içe gruplar, joker, dışlama, kesişim, döngüler, derinlik sınırı, revizyonlar, paylaşılan oturumlar),
rastgele oracle karşılaştırması, PostgreSQL deposu (revizyonlar, geçmiş, eşzamanlılık, değişiklik akışı, bellek deposuyla eşdeğerlik) ve HTTP API (kimlik doğrulama ve kapsamlar, doğrulama, iki düğümde tutarlılık, şema yayılımı).

## Mimari

```
istemci ─▶ HTTP API ─▶ HeadTracker (tutarlılık → revizyon) ─▶ Checker ─▶ CachingTupleReader ─▶ PostgreSQL
                          │                                     │          (anahtar: nesne, revizyon)      relationships
                          └ belirteç = "r{revizyon}"            └ oturum: memo + nesne başına tek okuma    revisions, schemas
```

Kararlar: [docs/adr](docs/adr).

## Bilinen sınırlar (dürüst liste)

- **Caveat (koşullu izin) yok**: öznitelik tabanlı koşullar ve süreli ilişkiler desteklenmiyor.
- **`ListObjects` bir taramadır:** en az bir ilişkisi olan tipteki her nesneyi, paylaşılan bir oturumla kontrol eder. Ters indeks yok; O(aday) olduğundan çok büyük nesne kümeleri için uygun değil.
- **Yazmalar serileştirilir:** revizyonların sırayla görünür olması için tek bir advisory kilit kullanılır. Basit ve doğru; yazma verimine tavan koyar. PostgreSQL işlem anlık görüntüleri (SpiceDB'nin yaptığı gibi) tavanı kaldırabilir.
- **Geçmiş hiç sıkıştırılmaz.** Eski revizyonlar okunabilsin diye silinen ilişkiler tabloda kalır; çöp toplama yok.
- **Tutarlılık belirteçleri düz revizyondur**: saat sınırlı veya imzalı değil. Başka bir dağıtımdan gelen belirteç fark edilmez.
- **Değişiklik akışı yoklamalıdır**, akış değil.
- **Şema değişiklikleri migrasyon değildir:** yeni şema yayınlandığında mevcut ilişkiler yeniden doğrulanmaz (şemanın artık izin vermediği kayıtlar check'te sadece etkisiz kalır).
- **Kimlik doğrulama statik API anahtarlarıdır**, üç kapsamla; nesne tipi başına yetkilendirme veya kiracı izolasyonu yok.
- Derinlik sınırı (varsayılan 32) çok derin hiyerarşiler için yükseltilmeli; aksi hâlde 422 döner.
- Ölçümler tek düğümde ve ret ağırlıklı rastgele iş yüküyle yapıldı; tekrarlı check'lerin gerçek trafiği daha iyi önbelleğe oturur.

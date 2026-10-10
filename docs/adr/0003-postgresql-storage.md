# ADR 0003 — PostgreSQL depolaması

**Durum:** Kabul edildi

- Tek tablo, `relationships`, tüm geçmişle: `(object_type, object_id, relation, subject_type, subject_id, subject_relation, created_rev, deleted_rev)`. **Canlı satırlarda kısmi benzersiz indeks** (`WHERE deleted_rev IS NULL`) "touch"ı veritabanı düzeyinde idempotent yapar; yine de bir ilişkinin silinip yeni satır olarak yeniden eklenmesine izin verir.
- Bir nesnenin bir revizyonda okunması `(object_type, object_id, relation, created_rev)` indeksini kullanır; özneye göre okuma ve değişiklik akışının kendi indeksleri vardır.
- **Yazmalar tek bir advisory kilitle serileştirilir** ve revizyon satırı işlemin sonunda eklenir. Yazıcılar çakışamadığından `N` revizyonu her zaman `N+1` başlamadan commit edilir; dolayısıyla `N` başını gören okuyucu `N`'e kadar her şeyi görür. (Kilitsiz bir sekanstan revizyon ayırmak, 5'in 4'ten önce commit olmasına yol açar ve baş 5'teki okuyucu 4'ü sessizce kaçırır.)
- Toplu yazmalar diziler üzerinde `unnest` kullanır ve aynı işlemin ardışık gruplarında tek ifade olarak çalışır; istek içindeki işlem sırası korunur (aynı kayıt için touch, delete, touch yazıldığı gibi davranır).
- Şema metni `schemas` tablosunda saklanır; düğümler yeni sürümü bir saniye içinde alır.
- Toplu yükleme hızı bir makinede ölçüldü: 10.000'lik gruplarla yaklaşık 67 bin ilişki/sn.

## Sonuçlar

- Basit ve doğruluğu kanıtlanabilir; yazma verimi tek yazıcıyla sınırlıdır.
- Eski revizyonların çöp toplaması yok (README sınırı).
- Bellek deposu aynı arayüzü ve anlambilimi uygular; PostgreSQL eşdeğerlik testini ve oracle testini mümkün kılan budur.

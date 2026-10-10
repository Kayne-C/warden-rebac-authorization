# ADR 0002 — Bir check nasıl değerlendirilir

**Durum:** Kabul edildi

## Karar

İzin bir ifade ağacıdır (`Ref`, `Arrow`, `Union`, `Intersection`, `Exclusion`); ilişki ise saklanan öznelerin kümesidir. `Check(nesne, ad, özne)` bunu özyinelemeli değerlendirir.

- **İlişkiler:** doğrudan özne, tip ve kimlik eşleşirse ya da saklanan özne joker `type:*` ise eşleşir. Küme özneleri (`group:eng#member`), aynı özne için o nesnede o ilişki değerlendirilerek izlenir.
- **Oklar** (`parent->view`): `parent` ilişkisinin her nesne öznesi için üzerinde `view` değerlendirilir. Adı tanımlamayan hedefler atlanır (şema doğrulayıcı, izinli tipler için bunun olamayacağını zaten garanti eder).
- **Birleşim**, ilk doğru dalda kısa devre yapar ve dalları (sınırlı) paralel çalıştırır. **Kesişim** ve **dışlama** önce solu değerlendirir, sağı yalnızca gerektiğinde.
- **Veride döngüler normaldir** (A grubu B'yi, B de A'yı içerir). Değerlendirilen `(nesne, ad)` çiftlerinin yolu izlenir; yolda olan bir anahtarla karşılaşmak **false** katkısı yapar. Bu, tekdüze (monoton) şemalar için en küçük sabit noktayı verir; oracle testi tam olarak bunu doğrular.
- **Memoization sağlam olmalı.** Bir döngü kesilirken hesaplanan sonuç hâlâ sürmekte olan bir atasına bağlı olabilir; bu yüzden sonuç yalnızca üstündeki çözülmemiş kesim noktaları işe karışmadıysa önbelleğe alınır. Her değerlendirme dokunduğu en sığ kesim derinliğini döndürür; bu derinlik kendisinden sığ değilse sonuç saklanır. Doğru (true) sonuçlar her zaman güvenlidir (kesimler yalnızca yol eler); sağ tarafı bir kesime dayanan bir dışlamanın ürettiği false güvenli değildir (oracle ve dışlama testleri kapsar).
- **Derinlik sınırı:** `MaxDepth` aşılırsa `DepthExceededException` fırlatılır (HTTP 422). Sessiz bir "ret" güvenlik açısından yanlış cevap olurdu; bu yüzden hatadır.
- **Gidiş-dönüşler:** bir nesnenin tüm ilişkileri tek okumada alınır ve paralel dallar arasında da tüm oturumda paylaşılır (`ConcurrentDictionary<nesne, Task<...>>`). Bu, kıyaslamada check başına okumayı 34'ten 12,8'e indirdi ve PostgreSQL'i 5 kat hızlandırdı.
- Bir **oturum** (tek özne, tek revizyon) çok sayıda check'i yanıtlayıp memo'sunu ve nesne okumalarını paylaşabilir; `ListObjects` bunu kullanır.

## Sonuçlar

- Doğruluk, döngülü olanlar dahil yüzlerce rastgele grafikte bağımsız bir aşağıdan-yukarıya uygulamaya karşı denetlenir.
- Döngü içeren veri üzerinde dışlama için "döngü false katkı yapar"ın ötesinde bir anlam verilmez; buna dayanan şemalardan kaçınılmalıdır.
- Paralel dallar, kardeşinin gereksiz kıldığı nesneleri okuyabilir; paylaşılan nesne okumaları bu maliyeti düşük tutar.

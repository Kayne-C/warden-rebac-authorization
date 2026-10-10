# ADR 0001 — Revizyonlar ve tutarlılık belirteçleri

**Durum:** Kabul edildi

## Bağlam

Yetkilendirmenin sıradan önbellekte olmayan bir hata biçimi vardır: "yeni düşman" problemi. Erişim geri alınıp ardından bir belge paylaşılırsa, paylaşımı taze bir kopyadan ama geri almayı eski bir kopyadan okuyan bir check, kalkmış olması gereken erişimi verebilir. Check'ler **tutarlı bir anlık görüntüden** okumalı ve çağıranlar "en az son yazmam kadar yeni" diyebilmelidir.

## Karar

- Bir şeyi değiştiren her yazma işlemi tek bir **revizyon** (monoton artan tamsayı) üretir. Bir ilişkinin `created_rev` ve `deleted_rev` alanları vardır; `R` revizyonunda yalnızca `created_rev <= R` ve (`deleted_rev` boş ya da `> R`) ise görünür. Hiçbir şey ezilmez; geçmiş her revizyon okunabilir.
- Bir check baştan sona **tek revizyonda** çalışır; dolayısıyla tüm okumaları birbiriyle uyumludur.
- Yazma bir belirteç (`r{revizyon}`) döner. Okumalar bir tutarlılık düzeyi seçer:
  - `minimize_latency` (varsayılan): bu düğümün gördüğü en yeni baş; en fazla `HeadCacheMs` aralığıyla tazelenir. Ucuz ve önbellek dostu, birkaç milisaniye eski olabilir.
  - `at_least_as_fresh(token)`: önbellekteki baş belirteçte ya da ötesindeyse o, değilse taze baş. Her çağrıda baş sorgusu ödemeden "yazdığını oku" garantisi.
  - `fully_consistent`: başı şimdi veritabanından iste.
- Revizyon yalnızca gerçekten bir şey değiştiğinde ayrılır. Var olan ilişkiye touch ya da olmayanı silmek mevcut başı döndürür.
- Önbellekler `(nesne, revizyon)` anahtarlıdır. Bir revizyon asla değişmez; bu yüzden önbellekteki veri, anahtarlandığı revizyon için asla bayat değildir. "Tazelik" yalnızca isteğin hangi revizyonu seçtiği sorusudur.

## Sonuçlar

- Erişimi geri alan yazmanın belirtecini sunan her istek, herhangi bir düğümde geri almayı görür (iki düğüm ve kasıtlı bayat baş önbelleğiyle test edildi).
- `minimize_latency` isteği biraz eski bir revizyona dayalı karar verebilir. Gecikme için belgelenmiş ödünleşim budur.
- Belirteçler düz revizyondur: imzalı değil, zamanla sınırlı değil (README sınırlarına bakın).

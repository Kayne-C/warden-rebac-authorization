# ADR 0004 — Şema dili ve doğrulaması

**Durum:** Kabul edildi

- SpiceDB'ye benzer küçük bir dil: `definition`, `relation ad: izinli | izinli`, `permission ad = ifade`. İzinli özneler `type`, `type#relation` (küme) veya `type:*` (joker) olabilir.
- **Elle yazılmış lexer ve özyinelemeli inişli ayrıştırıcı.** Öncelik: `&`, `+` ve `-`'den güçlü bağlanır; `+` ve `-` soldan birleşimlidir; parantez önceliği geçersiz kılar. Yorumlar (`//`, `/* */`) desteklenir. Sözdizimi hataları tam satır ve sütunu taşır, neyin beklendiğini ve neyin bulunduğunu söyler.
- **Doğrulama ayrı bir geçiştir** ve tüm anlamsal hataları birlikte bildirir: yinelenen tip, ilişki ve izinler; hem ilişki hem izin olan ad; izinli öznelerde bilinmeyen tip veya ilişki; bilinmeyen adlara başvurular; solu ilişki olmayan ya da sağı solun işaret edebildiği her tipte bulunmayan ok; ve veri üzerinden geçmeden kendi cinsinden tanımlanan izinler (`a = b`, `b = a`). **Veri üzerinden** özyineleme (`parent->view`) hiyerarşiler böyle çalıştığından serbesttir.
- **Yazmalar şemaya göre doğrulanır:** bir ilişki var olan bir ilişkiyi adlandırmalı, izne yazamamalı ve öznesi izinli biçimlerden biri olmalıdır (küme ya da joker yalnızca ilişki bunları listeliyorsa). Tek bir geçersiz güncelleme, tüm sorunları listeleyerek isteğin tamamını reddeder.
- Geçersiz şema yayınlamak `errors[{line, column, message}]` ile 400 döner ve etkin şemaya dokunmaz.

## Sonuçlar

- Yazarlar hataları check anında yanlış cevap olarak değil, yayın anında görür.
- Şemayı değiştirmek mevcut veriyi migrate etmez (README sınırı).

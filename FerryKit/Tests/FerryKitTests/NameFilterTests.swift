import Testing
@testable import FerryKit

struct NameFilterTests {
    @Test func emptyAndWhitespaceDoNotFilter() {
        for query in ["", " ", "   ", "\t", "　", " 　 "] {
            let filter = NameFilter(query)
            #expect(!filter.isActive)
            #expect(filter.matches("anything.jpg"))
        }
    }

    @Test func multipleTermsMustAllMatch() {
        let filter = NameFilter("img 2024")
        #expect(filter.terms == ["img", "2024"])
        #expect(filter.matches("IMG_2024_01.jpg"))
        #expect(filter.matches("2024-img.png"))
        #expect(!filter.matches("IMG_2023.jpg"))
        #expect(!filter.matches("photo-2024.jpg"))
        // 全角の空白・連続した空白も区切り
        #expect(NameFilter("img　 2024 ").terms == ["img", "2024"])
    }

    @Test func caseAndWidthInsensitive() {
        #expect(NameFilter("img").matches("IMG_001.jpg"))
        #expect(NameFilter("IMG").matches("img_001.jpg"))
        #expect(NameFilter("img").matches("ＩＭＧ_001.jpg"))
        #expect(NameFilter("ｉｍｇ").matches("IMG_001.jpg"))
        #expect(NameFilter("カタカナ").matches("ｶﾀｶﾅ.txt"))
        #expect(NameFilter("ｶﾀｶﾅ").matches("カタカナ.txt"))
        #expect(NameFilter("2024").matches("２０２４年.pdf"))
        #expect(NameFilter("cafe").matches("Café.txt"))
    }

    @Test func noMatch() {
        #expect(!NameFilter("xyz").matches("index.php"))
        // 濁点は区別する（「かそう」で「がぞう」は出さない）
        #expect(!NameFilter("かそう").matches("がぞう.png"))
        // 記号もそのまま部分一致
        #expect(NameFilter(".php").matches("wp-config.php"))
        #expect(!NameFilter(".php").matches("readme.txt"))
    }
}

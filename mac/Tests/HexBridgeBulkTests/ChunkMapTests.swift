import Foundation
import Testing

@testable import HexBridgeBulk

/// Что помнит принимающая сторона, пока объект едет.
///
/// Интересные случаи здесь не «пришёл блок» — они про то, что сеть отдаёт блоки
/// не по порядку, повторяет их и теряет, а карта при этом должна оставаться
/// дешёвой: на объекте в четыре гигабайта её размер и стоимость одного ответа
/// решают, состоится передача или нет.
@Suite("Карта пришедших блоков")
struct ChunkMapTests {

    @Test("Блоки приходят вразнобой и повторяются")
    func outOfOrderAndDuplicated() {
        var map = ChunkMap(count: 8)

        // Возвращаемое значение берётся в переменную, а не подставляется в
        // #expect: макрос раскрывается в замыкание над значением, а insert
        // меняет карту.
        var fresh: [Bool] = [map.insert(5), map.insert(1), map.insert(7)]
        #expect(fresh == [true, true, true])
        #expect(map.present == 3)

        // Повтор — обычное дело: отправляющая сторона переспрашивает по acknowledgement,
        // который успел устареть. Он не должен считаться вторым блоком, и именно
        // этот ответ не даёт записать его на диск дважды.
        fresh = [map.insert(5), map.insert(1)]
        #expect(fresh == [false, false])
        #expect(map.present == 3)

        #expect(map.contains(5))
        #expect(map.contains(0) == false)

        // Номера, которых у этого объекта нет, не принимаются и ничего не портят.
        fresh = [map.insert(8), map.insert(-1)]
        #expect(fresh == [false, false])
        #expect(map.present == 3)
    }

    @Test("Дырки перечисляются по порядку и не длиннее, чем просили")
    func holesComeOutInOrderAndBounded() {
        var map = ChunkMap(count: 16)
        for index in [0, 1, 2, 4, 5, 9] { map.insert(index) }

        #expect(map.missing(limit: 3) == [3, 6, 7])
        #expect(map.missing(limit: 100) == [3, 6, 7, 8, 10, 11, 12, 13, 14, 15])
        #expect(map.missing(limit: 0).isEmpty)
    }

    @Test("Последняя дырка закрывает объект")
    func theLastHoleCompletesTheObject() {
        var map = ChunkMap(count: 1000)
        for index in 0..<1000 where index != 617 { map.insert(index) }

        #expect(map.isComplete == false)
        #expect(map.present == 999)
        #expect(map.missing(limit: 257) == [617])

        let lastHole = map.insert(617)
        #expect(lastHole)
        #expect(map.isComplete)
        #expect(map.missing(limit: 257).isEmpty)
    }

    @Test("За концом объекта дырок не бывает")
    func nothingIsMissingPastTheEnd() {
        // 70 блоков — это два слова карты, из которых во втором заняты шесть
        // битов. Остальные пятьдесят восемь не блоки, и попросить их значит
        // попросить то, чего у второй стороны нет.
        var map = ChunkMap(count: 70)
        #expect(map.missing(limit: 1000) == Array(0..<70))

        for index in 0..<69 { map.insert(index) }
        #expect(map.missing(limit: 1000) == [69])
        map.insert(69)
        #expect(map.isComplete)
        #expect(map.missing(limit: 1000).isEmpty)
    }

    @Test("Карта, у которой блоков ровно на слово")
    func anExactNumberOfWords() {
        var map = ChunkMap(count: 128)
        for index in 0..<128 where index != 64 { map.insert(index) }
        #expect(map.missing(limit: 10) == [64])
        map.insert(64)
        #expect(map.isComplete)
    }

    @Test("Поиск первой дырки не сбивается, когда начало уже заполнено")
    func theScanKeepsUpWithAMovingFront() {
        // Поиск начинается не с нуля, а с отметки, оставленной прошлым разом.
        // Проверяется именно то, что отметка не перескакивает настоящую дырку.
        var map = ChunkMap(count: 4096)
        for index in 0..<1000 { map.insert(index) }
        #expect(map.missing(limit: 1).first == 1000)

        for index in 1001..<2000 { map.insert(index) }
        #expect(map.missing(limit: 1).first == 1000)

        map.insert(1000)
        #expect(map.missing(limit: 1).first == 2000)
    }

    @Test("Сброс возвращает карту в исходное состояние")
    func removeAllForgetsEverything() {
        var map = ChunkMap(count: 300)
        for index in 0..<300 { map.insert(index) }
        #expect(map.isComplete)

        // Хеш не сошёлся: объект просят заново целиком, и отметка первой дырки
        // обязана уехать обратно в ноль вместе с битами.
        map.removeAll()
        #expect(map.present == 0)
        #expect(map.isComplete == false)
        #expect(map.missing(limit: 3) == [0, 1, 2])
    }

    @Test("Карта стоит бит на блок, а не байт на байт объекта")
    func storageIsOneBitPerChunk() {
        // Потолок формата: 4 ГиБ без байта при блоке в 1024 байта.
        let chunks = 4 * 1024 * 1024
        let map = ChunkMap(count: chunks)

        #expect(map.storageBytes == chunks / 8)
        #expect(map.storageBytes == 512 * 1024)
        // Ради чего всё: у очевидной реализации на этом месте четыре гигабайта.
        #expect(map.storageBytes < chunks * 1024 / 1000)
    }

    @Test("Крайние размеры карты")
    func degenerateSizes() {
        var empty = ChunkMap(count: 0)
        #expect(empty.isComplete)
        #expect(empty.missing(limit: 10).isEmpty)
        #expect(empty.storageBytes == 0)

        var single = ChunkMap(count: 1)
        #expect(single.missing(limit: 10) == [0])
        let onlyChunk = single.insert(0)
        #expect(onlyChunk)
        #expect(single.isComplete)
    }

    @Test("Ответ на объекте в четыре миллиона блоков остаётся дешёвым")
    func askingForHolesStaysCheapAtTheCeiling() {
        // Не замер времени — проверка формы: карта на четыре миллиона блоков
        // собирается, заполняется и отвечает, и ответ ограничен тем, что
        // помещается в один acknowledgement, а не числом блоков.
        var map = ChunkMap(count: 4 * 1024 * 1024)
        for index in 0..<100_000 { map.insert(index) }

        let holes = map.missing(limit: 257)
        #expect(holes.count == 257)
        #expect(holes.first == 100_000)
        #expect(holes.last == 100_256)
    }
}

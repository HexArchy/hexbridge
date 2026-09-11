import Foundation
import Testing

@testable import HexBridgeBulk

/// Как находится скорость отправки.
///
/// Проверяется не «быстро или медленно», а форма петли: растёт, пока всё
/// доезжает, падает, когда перестаёт, и ни при каких ответах второй стороны не
/// выходит ни за пол, ни за потолок. Без первого «без ограничения» означает
/// скорость цикла, а не скорость канала; без второго один потерянный блок
/// оставляет большой файл ползти до утра.
@Suite("Темп отправки")
struct BulkPacerTests {

    @Test("Начинает с осторожной скорости")
    func startsGently() {
        let pacer = BulkPacer()
        #expect(pacer.chunksPerSecond == BulkPacer.openingRate)
    }

    @Test("Чистый круг прибавляет скорость")
    func cleanRoundsSpeedItUp() {
        var pacer = BulkPacer()
        let before = pacer.chunksPerSecond

        pacer.note(sent: 1000, lost: 0)
        #expect(pacer.chunksPerSecond == before + BulkPacer.increasePerCleanRound)

        pacer.note(sent: 1000, lost: 0)
        pacer.note(sent: 1000, lost: 0)
        #expect(pacer.chunksPerSecond == before + 3 * BulkPacer.increasePerCleanRound)
    }

    @Test("Круг с потерями сбавляет скорость")
    func lossyRoundsSlowItDown() {
        var pacer = BulkPacer()
        for _ in 0..<20 { pacer.note(sent: 1000, lost: 0) }
        let fast = pacer.chunksPerSecond

        pacer.note(sent: 1000, lost: 200)
        #expect(pacer.chunksPerSecond < fast)
        #expect(abs(pacer.chunksPerSecond - fast * BulkPacer.decreaseFactor) < 0.001)
    }

    @Test("Одна потеря на двадцать тысяч блоков — это не перегрузка")
    func aSingleStrayLossIsNotCongestion() {
        // Тот самый порог, ради которого «без ограничения» вообще имеет смысл:
        // идеальных сетей нет, и если считать перегрузкой любую потерю, скорость
        // никогда не уйдёт от начальной.
        var pacer = BulkPacer()
        let before = pacer.chunksPerSecond
        pacer.note(sent: 20_000, lost: 1)
        #expect(pacer.chunksPerSecond > before)

        // А одна на пятьдесят — уже перегрузка: пересылки начинают стоить
        // дороже, чем стоит скорость.
        let grown = pacer.chunksPerSecond
        pacer.note(sent: 20_000, lost: 400)
        #expect(pacer.chunksPerSecond < grown)
    }

    @Test("Круг, в котором ничего не отправляли, ничего не решает")
    func silenceIsNotEvidence() {
        var pacer = BulkPacer()
        let before = pacer.chunksPerSecond
        pacer.note(sent: 0, lost: 0)
        pacer.note(sent: 0, lost: 5)
        #expect(pacer.chunksPerSecond == before)
    }

    @Test("Ниже пола не опускается")
    func itNeverGoesBelowTheFloor() {
        var pacer = BulkPacer()
        for _ in 0..<200 { pacer.note(sent: 1000, lost: 900) }
        #expect(pacer.chunksPerSecond == BulkPacer.floorRate)
    }

    @Test("Выше жёсткого потолка не поднимается")
    func itNeverGoesAboveTheHardCeiling() {
        var pacer = BulkPacer()
        // На петле обратной связи, которой никто не возражает — шлейф, локальная
        // петля — прибавка ничем не уравновешена, и без этого потолка один тик
        // отдал бы сокету миллион блоков.
        for _ in 0..<10_000 { pacer.note(sent: 10_000, lost: 0) }
        #expect(pacer.chunksPerSecond == BulkPacer.hardCeiling)
    }

    @Test("Заданный потолок держится")
    func aCeilingThatWasAskedForIsKept() {
        var pacer = BulkPacer(ceiling: 2048)
        for _ in 0..<100 { pacer.note(sent: 1000, lost: 0) }
        #expect(pacer.chunksPerSecond == 2048)

        // Потолок опустили, пока передача идёт: скорость обязана опуститься
        // сразу, а не дождавшись следующей потери.
        pacer.ceiling = 512
        #expect(pacer.chunksPerSecond == 512)

        pacer.ceiling = nil
        pacer.note(sent: 1000, lost: 0)
        #expect(pacer.chunksPerSecond > 512)
    }

    @Test("Через релей не целятся выше того, что он увозит")
    func theRelayCeilingLeavesRoomForVoice() {
        // Релей отдаёт 20 000 пакетов в секунду на конец и остальное роняет —
        // это его нынешнее умолчание, поднятое с 2000, когда по тому же сокету
        // поехал файл. Звук и геймпад живут там же, поэтому потолок ниже.
        #expect(BulkPacer.relayCeiling < 20_000)

        var pacer = BulkPacer(ceiling: BulkPacer.relayCeiling)
        for _ in 0..<100 { pacer.note(sent: 1000, lost: 0) }
        #expect(pacer.chunksPerSecond == BulkPacer.relayCeiling)
    }

    @Test("Ведро наливается по скорости и не копит больше всплеска")
    func theBucketFillsAtTheRateAndStopsAtOneBurst() {
        var pacer = BulkPacer(ceiling: 1000)

        // Ничего не прошло — ничего и не накапало.
        #expect(pacer.take() == false)

        pacer.advance(by: 0.02)
        var taken = 0
        while pacer.take() { taken += 1 }
        #expect(taken == 20)

        // Таймер опоздал на целую секунду. Ведро мелкое ровно затем, чтобы это
        // не вылилось в сокет одним залпом — иначе выходит та самая перегрузка,
        // от которой всё остальное здесь и защищает.
        pacer.advance(by: 1)
        taken = 0
        while pacer.take() { taken += 1 }
        #expect(taken <= Int(1000 * BulkPacer.burstWindow) + 1)
    }

    @Test("Новый сокет — новый разгон")
    func rebindingStartsOver() {
        var pacer = BulkPacer()
        for _ in 0..<10 { pacer.note(sent: 1000, lost: 0) }
        pacer.advance(by: 1)
        #expect(pacer.chunksPerSecond > BulkPacer.openingRate)

        pacer.restart()
        #expect(pacer.chunksPerSecond == BulkPacer.openingRate)
        // Кредит, заработанный на канале, которого больше нет, — не кредит.
        #expect(pacer.take() == false)
    }

    @Test("Отдых сбрасывает накопленное, но не скорость")
    func restingDropsCreditAndKeepsTheRate() {
        var pacer = BulkPacer()
        pacer.note(sent: 1000, lost: 0)
        let rate = pacer.chunksPerSecond
        pacer.advance(by: 0.05)
        pacer.rest()
        #expect(pacer.take() == false)
        #expect(pacer.chunksPerSecond == rate)
    }
}

namespace FormatFactoryWinUI.Services;

internal static class ResultSummaryLocalizationResources
{
    internal static readonly LocalizedResourceModule Module = new("ResultSummary", [
        new("ResultSummaryReduced", ["{0}% smaller", "减少 {0}%", "減少 {0}%", "{0}% 縮小", "{0}% 감소", "{0}% menos", "{0}% de moins", "{0}% kleiner", "{0}% menor", "На {0}% меньше", "{0}% più piccolo", "{0}% kleiner", "O {0}% mniej", "%{0} daha küçük", "أصغر بنسبة {0}%"]),
        new("ResultSummaryIncreased", ["{0}% larger", "增加 {0}%", "增加 {0}%", "{0}% 増加", "{0}% 증가", "{0}% más", "{0}% de plus", "{0}% größer", "{0}% maior", "На {0}% больше", "{0}% più grande", "{0}% groter", "O {0}% więcej", "%{0} daha büyük", "أكبر بنسبة {0}%"]),
        new("ResultSummaryUnchanged", ["Size unchanged", "体积不变", "大小不變", "サイズ変更なし", "크기 변경 없음", "Mismo tamaño", "Taille inchangée", "Größe unverändert", "Tamanho inalterado", "Размер не изменился", "Dimensione invariata", "Grootte ongewijzigd", "Rozmiar bez zmian", "Boyut değişmedi", "الحجم لم يتغير"]),
        new("ResultSummaryElapsed", ["Elapsed {0}", "耗时 {0}", "耗時 {0}", "所要時間 {0}", "소요 시간 {0}", "Tiempo {0}", "Durée {0}", "Dauer {0}", "Duração {0}", "Время {0}", "Durata {0}", "Duur {0}", "Czas {0}", "Süre {0}", "المدة {0}"]),
        new("ResultSummaryBatch", ["{0} completed · {1} failed · {2} cancelled", "成功 {0} · 失败 {1} · 取消 {2}", "成功 {0} · 失敗 {1} · 取消 {2}", "完了 {0} · 失敗 {1} · キャンセル {2}", "완료 {0} · 실패 {1} · 취소 {2}", "{0} completadas · {1} fallidas · {2} canceladas", "{0} terminées · {1} échouées · {2} annulées", "{0} abgeschlossen · {1} fehlgeschlagen · {2} abgebrochen", "{0} concluídas · {1} falhas · {2} canceladas", "Готово: {0} · Ошибки: {1} · Отменено: {2}", "{0} completate · {1} non riuscite · {2} annullate", "{0} voltooid · {1} mislukt · {2} geannuleerd", "Ukończono {0} · Błędy {1} · Anulowano {2}", "{0} tamamlandı · {1} başarısız · {2} iptal edildi", "اكتمل {0} · فشل {1} · أُلغي {2}"])
    ]);
}

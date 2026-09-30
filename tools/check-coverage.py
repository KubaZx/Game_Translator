#!/usr/bin/env python3
"""Próg pokrycia linii per assembly na podstawie raportu Cobertura.

Użycie:
    python3 tools/check-coverage.py <Cobertura.xml> Pakiet=procent [Pakiet=procent ...]

Czytamy scalony raport z ReportGeneratora (a nie surowe pliki coverlet), bo Core
jest pokrywany przez oba projekty testowe — dopiero suma daje prawdziwy obraz.
Progi ustawiamy kilka punktów PONIŻEJ zmierzonych wartości: mają łapać wyraźny
spadek (np. nowy moduł bez testów), a nie blokować PR-a o ułamek procenta.
Brak pakietu w raporcie też jest błędem — inaczej literówka w nazwie cicho
wyłączyłaby kontrolę.
"""

import sys
import xml.etree.ElementTree as ET


def main(argv: list[str]) -> int:
    if len(argv) < 3:
        print(__doc__)
        return 2

    report, specs = argv[1], argv[2:]
    rates = {
        package.get("name"): float(package.get("line-rate", "0")) * 100
        for package in ET.parse(report).getroot().iter("package")
    }

    failed = False
    for spec in specs:
        name, _, minimum_text = spec.partition("=")
        minimum = float(minimum_text)
        actual = rates.get(name)
        if actual is None:
            print(f"::error::Brak pakietu {name} w raporcie pokrycia {report}")
            failed = True
            continue

        status = "OK" if actual >= minimum else "ZA MAŁO"
        print(f"{name}: {actual:.2f}% linii (próg {minimum:.0f}%) — {status}")
        if actual < minimum:
            print(f"::error::Pokrycie linii {name} spadło do {actual:.2f}% (próg {minimum:.0f}%)")
            failed = True

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

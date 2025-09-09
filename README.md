# B2Extractor



**B2IndexExtractor** to narzędzie w C#/.NET (WPF) do wypakowywania zawartości z plików **.b2index** i powiązanych kontenerów **.b2container** (gier Gears 5 i Gears Tactics).  

Projekt automatycznie odtwarza strukturę plików, obsługuje dekompresję Oodle oraz zawiera mechanizmy odzyskiwania pełnych ścieżek.



---



## Funkcje



- 📦 **Ekstrakcja plików** z `.b2index` i powiązanych `.b2container`.

- 🔄 **Obsługa Oodle (oo2core_7_win64.dll)**

- 📂 **Odzyskiwanie pełnych ścieżek**:

	- Na podstawie nagłówków plików `.uasset/.umap`.

	- Przy użyciu heurystyk analizy zawartości.

	- Automatyczne porządkowanie plików `ubulk` obok odpowiadających im `uasset`.

- 🧹 **Drugi pass dla UBULK** – przenoszenie osieroconych plików obok ich właścicieli.

- 🎛️ **Opcje filtrowania**:

- Pomijanie plików WEM

- Pomijanie plików Bink

- Pomijanie plików konfiguracyjnych

- Tryb *Only Assets*



- 📝 **Logowanie**:

&nbsp; - Log na żywo w GUI (20 ostatnich linii).

&nbsp; - Pełny log sesji w pliku.



---



## Wymagania

- **.NET 6/7/8+** (WPF)

- Plik **oo2core_7_win64.dll** w katalogu programu



---



## Użycie



1. Uruchom aplikację `B2IndexExtractor.exe`.

2. Wskaż plik `.b2index`.

3. Wybierz katalog wyjściowy.

4. Skonfiguruj opcje (np. pomijanie WEM, Bink, configów, tryb \*Only Assets\*).

5. Kliknij **Extract**.

6. Postęp i logi widoczne są w oknie programu oraz w pliku logu.



---



## Licencja



Ten projekt jest objęty licencją **GNU General Public License v3.0**.  

Szczegóły znajdziesz w pliku [LICENSE](LICENSE).




# B2Extractor

## 📥 Download

You can download the latest release here:  
👉 [Latest Release](https://github.com/bmbr1990/B2Extractor/releases/latest)


# EN:

**B2IndexExtractor** is a C#/.NET (WPF) tool for extracting the contents of **.b2index** files and related **.b2container** archives (from the games Gears 5 and Gears Tactics).  

The project automatically reconstructs the file structure, supports Oodle decompression, and includes mechanisms for recovering full file paths.

---

## Features

- 📦 **File extraction** from `.b2index` and associated `.b2container`.
- 🔄 **Oodle support (oo2core_7_win64.dll)**
- 📂 **Full path recovery**.
- 🎛️ **Filtering options**:
  - Skip WEM files
  - Skip Bink files
  - Skip configuration files
  - *Only Assets* mode
- 📝 **Logging**:
  - Live log in the GUI
  - Full session log saved to file.

---

## Requirements

- **.NET 6/7/8+** (WPF)
- **oo2core_7_win64.dll** file in the program directory - can be found in modern games like Elden Ring or Smire, or downloaded from Google ( on your own responsibility )

---

## Usage

1. Run `B2IndexExtractor.exe`.
2. Select a `.b2index` file.
3. Choose the output directory.
4. Configure options (e.g., skip WEM, Bink, configs, or enable *Only Assets* mode).
5. Click **Extract**.
6. Progress and logs are displayed in the program window and saved to a log file.

---

## License

This project is licensed under the **GNU General Public License v3.0**.  
See the [LICENSE.txt](LICENSE.txt) file for details.

---

# PL:

**B2IndexExtractor** to narzędzie w C#/.NET (WPF) do wypakowywania zawartości z plików **.b2index** i powiązanych kontenerów **.b2container** (gier Gears 5 i Gears Tactics).  
Projekt automatycznie odtwarza strukturę plików, obsługuje dekompresję Oodle oraz zawiera mechanizmy odzyskiwania pełnych ścieżek.

## 📥 Download

Możesz pobrać aktualną wersje pod tym linkiem:  
👉 [Aktualna Wersja](https://github.com/bmbr1990/B2Extractor/releases/latest)

## Funkcje

- 📦 **Ekstrakcja plików** z `.b2index` i powiązanych `.b2container`.
- 🔄 **Obsługa Oodle (oo2core_7_win64.dll)**
- 📂 **Odzyskiwanie pełnych ścieżek**.
- 🎛️ **Opcje filtrowania**:
	- Pomijanie plików WEM
	- Pomijanie plików Bink
	- Pomijanie plików konfiguracyjnych
	- Tryb *Only Assets*
- 📝 **Logowanie**:
	- Log na żywo w GUI.
	- Pełny log sesji w pliku.

---

## Wymagania

- **.NET 6/7/8+** (WPF)
- Plik **oo2core_7_win64.dll** w katalogu programu, Można znaleźć ten plik w grach wydanych po 2018 jak Smite, Elden Ring albo można wyszukać w google (na własną odpowiedzialność)

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
Szczegóły znajdziesz w pliku [LICENSE.txt](LICENSE.txt).




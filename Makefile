# Camp Ledger: one build for the reader, the launcher ASI and the web app.
#
#   make            build everything into dist/
#   make reader     dist/reader/CampLedgerReader.exe (+ offsets/)
#   make launcher   dist/launcher/CampLedgerLauncher.asi + CampLedgerLauncher.ini
#   make web        dist/web/ (skipped with a message until web/ exists)
#   make test       run every test project
#   make clean      remove dist/ and the bin/obj folders
#
# Needs: GNU make, the .NET 8 SDK, Zig 0.17+ (for the launcher), and a POSIX sh (Git for Windows).
# Nothing here touches the game folder. Copying the launcher into <install>\bin64 stays a manual step
# for the owner (AGENTS.md hard rule 1, launcher-only exception).

SHELL := sh
.SHELLFLAGS := -ec

CONFIG  ?= Release
RID     ?= win-x64
DOTNET  ?= dotnet
ZIG     ?= zig
DIST    := dist

READER_PROJ  := reader/CampLedger.Reader/CampLedger.Reader.csproj
WEB_PROJ     := $(wildcard web/CampLedger.Web/CampLedger.Web.csproj)
TEST_PROJS   := core/CampLedger.Core.Tests reader/CampLedger.Reader.Tests

READER_OUT   := $(DIST)/reader
READER_EXE   := $(READER_OUT)/CampLedgerReader.exe
LAUNCHER_OUT := $(DIST)/launcher
LAUNCHER_SRC := launcher/CampLedgerLauncher.c
LAUNCHER_ASI := $(LAUNCHER_OUT)/CampLedgerLauncher.asi
LAUNCHER_INI := $(LAUNCHER_OUT)/CampLedgerLauncher.ini
WEB_OUT      := $(DIST)/web

# Windows-style absolute path to the built reader, written into the launcher's ini.
READER_WINPATH := $(subst /,\,$(CURDIR)/$(READER_EXE))

.PHONY: all reader launcher web test clean check-tools

all: reader launcher web
	@echo
	@echo "Built into $(DIST)/:"
	@echo "  reader    $(READER_EXE)"
	@echo "  launcher  $(LAUNCHER_ASI) (+ .ini -> $(READER_WINPATH))"
	@if [ -n "$(WEB_PROJ)" ]; then echo "  web       $(WEB_OUT)/"; else echo "  web       not built (no web/ project yet)"; fi

check-tools:
	@command -v $(DOTNET) >/dev/null || { echo "missing: .NET 8 SDK ($(DOTNET))"; exit 1; }
	@command -v $(ZIG) >/dev/null || { echo "missing: zig (winget install zig.zig)"; exit 1; }
	@echo "dotnet $$($(DOTNET) --version), zig $$($(ZIG) version)"

# --- Reader -------------------------------------------------------------------------------------
# Framework-dependent single-file exe; offsets/*.json are copied beside it by the csproj.
reader:
	$(DOTNET) publish $(READER_PROJ) -c $(CONFIG) -r $(RID) --self-contained false \
		-p:PublishSingleFile=true -p:DebugType=none -o $(READER_OUT) --nologo

# --- Launcher ASI -------------------------------------------------------------------------------
launcher: $(LAUNCHER_ASI) $(LAUNCHER_INI)

$(LAUNCHER_ASI): $(LAUNCHER_SRC)
	@mkdir -p $(LAUNCHER_OUT)
	$(ZIG) cc -shared -O2 -target x86_64-windows-gnu $< -o $@ -lkernel32 -luser32
	@rm -f $(LAUNCHER_OUT)/*.lib $(LAUNCHER_OUT)/*.pdb

# The ini always points at the reader this Makefile builds. Rewritten every run in case the
# checkout moved.
$(LAUNCHER_INI): FORCE
	@mkdir -p $(LAUNCHER_OUT)
	@printf '%s\r\n' '$(READER_WINPATH)' > $@
	@echo "wrote $@ -> $(READER_WINPATH)"

# --- Web app ------------------------------------------------------------------------------------
# Stack is ASP.NET Core (docs/architecture.md); the project does not exist yet (milestone 4).
web:
ifeq ($(WEB_PROJ),)
	@echo "web: no web/CampLedger.Web project yet (milestone 4); skipping"
else
	$(DOTNET) publish $(WEB_PROJ) -c $(CONFIG) -o $(WEB_OUT) --nologo
endif

# --- Tests and cleanup --------------------------------------------------------------------------
test:
	@for p in $(TEST_PROJS) $(wildcard web/*.Tests); do \
		echo "== $$p"; $(DOTNET) test $$p -c $(CONFIG) --nologo; \
	done

clean:
	rm -rf $(DIST)
	find core reader tools web -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true

FORCE:

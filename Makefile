PROJECT_DIR := Source/YART
PROJECT_FILE := $(PROJECT_DIR)/YART.csproj
OLD_PROJECT_FILE := Source/YART.Old/YART.Old.csproj
SOLUTION_FILE := $(PROJECT_DIR)/YART.sln
DOTNET := dotnet
MSBUILD := C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe
CONFIGURATION := Release
OLD_CONFIGURATION := Release-1.6

.PHONY: all build build-old clean clean-old

all: build build-old

build:
	$(DOTNET) build $(PROJECT_FILE) -c $(CONFIGURATION) --no-incremental

build-old:
	"$(MSBUILD)" $(OLD_PROJECT_FILE) -restore -t:Rebuild -p:Configuration=$(OLD_CONFIGURATION) -p:Platform=AnyCPU -nologo -v:m

clean:
	$(DOTNET) clean $(PROJECT_FILE) -c $(CONFIGURATION)

clean-old:
	"$(MSBUILD)" $(OLD_PROJECT_FILE) -t:Clean -p:Configuration=$(OLD_CONFIGURATION) -p:Platform=AnyCPU -nologo -v:m

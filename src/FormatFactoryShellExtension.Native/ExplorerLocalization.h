#pragma once
#include "ExplorerLanguageData.h"

namespace ff {
// Windows supplies preferred UI language tags in order, separated by nulls.
const ExplorerLanguage& ResolveExplorerLanguage(std::wstring_view preferredLanguages);
}

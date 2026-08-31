#include <Windows.h>
#include <dia2.h>
#include <diacreate.h>

#include <iomanip>
#include <iostream>
#include <string>
#include <vector>

template <typename T>
class ComPtr {
 public:
  ~ComPtr() {
    if (value_ != nullptr) value_->Release();
  }

  T** Put() { return &value_; }
  T* Get() const { return value_; }
  T* operator->() const { return value_; }

 private:
  T* value_ = nullptr;
};

static std::wstring TakeBstr(BSTR value) {
  if (value == nullptr) return {};
  std::wstring result(value, SysStringLen(value));
  SysFreeString(value);
  return result;
}

static void PrintMatches(IDiaSymbol* global, enum SymTagEnum tag,
                         const wchar_t* pattern) {
  ComPtr<IDiaEnumSymbols> symbols;
  const bool enumerate_all = wcscmp(pattern, L"__ALL__") == 0;
  const DWORD options = enumerate_all ? nsNone : nsCaseInsensitive;
  HRESULT hr = global->findChildren(tag, enumerate_all ? nullptr : pattern,
                                    options, symbols.Put());
  if (FAILED(hr) || symbols.Get() == nullptr) return;

  LONG count = 0;
  symbols->get_Count(&count);
  std::wcout << L"tag=" << static_cast<unsigned>(tag) << L"\tcount=" << count
             << L"\n";
  const LONG limit = enumerate_all && count > 40 ? 40 : count;
  for (LONG index = 0; index < limit; ++index) {
    ComPtr<IDiaSymbol> symbol;
    ULONG fetched = 0;
    if (symbols->Next(1, symbol.Put(), &fetched) != S_OK || fetched != 1) break;

    DWORD rva = 0;
    ULONGLONG length = 0;
    BSTR name = nullptr;
    BSTR undecorated = nullptr;
    symbol->get_relativeVirtualAddress(&rva);
    symbol->get_length(&length);
    symbol->get_name(&name);
    symbol->get_undecoratedName(&undecorated);

    std::wcout << L"tag=" << static_cast<unsigned>(tag) << L"\trva=0x"
               << std::hex << std::uppercase << rva << L"\tlength=0x" << length
               << std::dec << L"\tname=" << TakeBstr(name)
               << L"\tundecorated=" << TakeBstr(undecorated) << L"\n";
  }
}

static void PrintTypeMembers(IDiaSymbol* global, const wchar_t* name) {
  ComPtr<IDiaEnumSymbols> types;
  HRESULT hr = global->findChildren(SymTagUDT, name, nsCaseInsensitive,
                                    types.Put());
  if (FAILED(hr) || types.Get() == nullptr) return;

  LONG count = 0;
  types->get_Count(&count);
  std::wcout << L"type=" << name << L"\tcount=" << count << L"\n";
  for (LONG type_index = 0; type_index < count; ++type_index) {
    ComPtr<IDiaSymbol> type;
    ULONG fetched = 0;
    if (types->Next(1, type.Put(), &fetched) != S_OK || fetched != 1) break;

    ULONGLONG type_length = 0;
    BSTR type_name = nullptr;
    type->get_length(&type_length);
    type->get_name(&type_name);
    std::wcout << L"udt=" << TakeBstr(type_name) << L"\tlength=0x" << std::hex
               << type_length << std::dec << L"\n";

    ComPtr<IDiaEnumSymbols> members;
    if (FAILED(type->findChildren(SymTagData, nullptr, nsNone, members.Put())) ||
        members.Get() == nullptr) {
      continue;
    }
    LONG member_count = 0;
    members->get_Count(&member_count);
    for (LONG member_index = 0; member_index < member_count; ++member_index) {
      ComPtr<IDiaSymbol> member;
      ULONG member_fetched = 0;
      if (members->Next(1, member.Put(), &member_fetched) != S_OK ||
          member_fetched != 1) {
        break;
      }
      BSTR member_name = nullptr;
      BSTR member_type_name = nullptr;
      LONG offset = 0;
      DWORD data_kind = 0;
      DWORD location_type = 0;
      DWORD bit_position = 0;
      ULONGLONG member_type_length = 0;
      ULONGLONG member_length = 0;
      ComPtr<IDiaSymbol> member_type;
      member->get_name(&member_name);
      member->get_offset(&offset);
      member->get_dataKind(&data_kind);
      member->get_locationType(&location_type);
      member->get_bitPosition(&bit_position);
      member->get_length(&member_length);
      if (SUCCEEDED(member->get_type(member_type.Put())) &&
          member_type.Get() != nullptr) {
        member_type->get_name(&member_type_name);
        member_type->get_length(&member_type_length);
      }
      std::wcout << L"member=" << TakeBstr(member_name) << L"\toffset=0x"
                 << std::hex << offset << std::dec << L"\tdataKind="
                 << data_kind << L"\ttype=" << TakeBstr(member_type_name)
                 << L"\ttypeLength=0x" << std::hex << member_type_length
                 << std::dec << L"\tlocation=" << location_type
                 << L"\tbitPosition=" << bit_position << L"\tmemberLength="
                 << member_length << L"\n";
    }
  }
}

int wmain(int argc, wchar_t** argv) {
  if (argc < 4) {
    std::wcerr << L"Usage: DiaSymbolQuery.exe <msdia.dll> <chrome.dll.pdb> "
                  L"<regex> [regex...]\n";
    return 2;
  }

  ComPtr<IDiaDataSource> source;
  HRESULT hr = NoRegCoCreate(argv[1], __uuidof(DiaSource),
                             __uuidof(IDiaDataSource),
                             reinterpret_cast<void**>(source.Put()));
  if (FAILED(hr)) {
    std::wcerr << L"NoRegCoCreate failed: 0x" << std::hex
               << static_cast<unsigned long>(hr) << L"\n";
    return 3;
  }

  hr = source->loadDataFromPdb(argv[2]);
  if (FAILED(hr)) {
    std::wcerr << L"loadDataFromPdb failed: 0x" << std::hex
               << static_cast<unsigned long>(hr) << L"\n";
    return 4;
  }

  ComPtr<IDiaSession> session;
  hr = source->openSession(session.Put());
  if (FAILED(hr)) {
    std::wcerr << L"openSession failed: 0x" << std::hex
               << static_cast<unsigned long>(hr) << L"\n";
    return 5;
  }

  ComPtr<IDiaSymbol> global;
  hr = session->get_globalScope(global.Put());
  if (FAILED(hr)) {
    std::wcerr << L"get_globalScope failed: 0x" << std::hex
               << static_cast<unsigned long>(hr) << L"\n";
    return 6;
  }

  for (int index = 3; index < argc; ++index) {
    std::wcout << L"pattern=" << argv[index] << L"\n";
    PrintMatches(global.Get(), SymTagFunction, argv[index]);
    PrintMatches(global.Get(), SymTagPublicSymbol, argv[index]);
    PrintTypeMembers(global.Get(), argv[index]);
  }
  return 0;
}

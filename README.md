# FLOW 구현 설계

## 개발 환경

기존 설계대로 C# / .NET 8 / WPF, Windows x64로 시작한다. 이 골격에는 두 WPF 진입점, 공용 매니페스트 모델, 검증된 로컬 콘텐츠를 기준으로 하는 변경 계획 함수가 들어 있다. 아래 나머지 구성은 구현 명세다.

```powershell
dotnet publish Launcher/Launcher.csproj -c Release -r win-x64 --self-contained true -o build/launcher
dotnet publish Publisher/Publisher.csproj -c Release -r win-x64 --self-contained true -o build/publisher
```

## GDLauncher 없는 최초 설치

FLOW Launcher는 자체 설치 경로와 자체 Microsoft 로그인 세션을 사용한다. 최초 실행 시 공개 배포 매니페스트에서 Sunlit Valley를 설치하고, 지정된 x64 Java 런타임을 검증해 FLOW 전용 캐시에 설치한다. 공식 Minecraft 배포처와 Forge 배포처에서 해당 프로필을 준비한 뒤 독립 gameDir로 Java를 직접 실행한다. GDLauncher의 설치 경로, 실행 파일, Java, 토큰, 캐시 및 기존 게임 프로필은 런타임 의존성에서 제외한다. Publisher 역시 별도로 보관한 원본 폴더만 사용한다. 기존 월드를 가져오려는 사용자는 원본 삭제 전에 별도로 백업한다.

## 배포 형식

팩 전용 공개 GitHub 저장소의 최신 일반 릴리스를 stable 채널로 사용한다. 태그 형식은 sunlit-valley-<sequence>로 고정한다. /releases/latest에서 릴리스를 찾고 그 릴리스의 manifest.json과 manifest.sig를 가져온다. manifest.sig는 원본 JSON 바이트에 대한 ECDSA P-256/SHA-256 DER 서명을 Base64로 저장한 텍스트다. 런처 내장 공개키로 서명을 확인한 뒤 파싱한다. 서명키는 Publisher에서만 Windows 인증서 저장소의 비내보내기 키로 사용한다. 키 교체는 새 공개키를 포함한 런처가 먼저 배포되어야 한다.

JSON은 UTF-8, camelCase다. schemaVersion=1, packId=sunlit-valley, sequence=양의 증가 정수, packVersion 및 minimumLauncherVersion=major.minor.patch. game에는 minecraftVersion=1.20.1, loader=forge, loaderVersion=47.4.0 (원본 메타데이터 기준), javaMajor=17, dataEpoch=초기값 1(문자열)을 둔다. files에는 전체 관리 파일의 path, 소문자 SHA-256 64자리 sha256, 바이트 수 size, blob의 tag 및 asset을 담는다. GitHub 저장소는 런처 설정에 고정한다. 자산명은 sha256-<hash>.bin이다. 같은 해시는 같은 자산을 참조한다.

새 버전에는 기존에 게시한 동일 해시 자산을 재사용하고 신규 해시만 업로드한다. 따라서 첫 설치, 중간 버전 건너뛰기, 손상 복구가 모두 목표 매니페스트 하나로 가능하다. 현재 배포에서 참조하는 이전 릴리스는 보존한다. 삭제는 대상 목록에서 경로를 제외하는 것으로 표현한다. 내려받기 작업은 해시별로 중복 제거한다. 개별 자산은 2 GiB 미만이어야 하며 초기 구현은 초과 파일의 게시를 검사 단계에서 중단한다. 모드별 재배포 권한과 출처를 게시 목록에 기록하고, 직접 배포할 수 없는 필수 파일은 해당 공급자 다운로드 연동을 구현하기 전까지 게시를 중단한다.

## Publisher

1. 독립 Sunlit Valley 원본 폴더와 정확한 게임 프로필을 선택한다. 원본이 GDLauncher 안에만 있다면 삭제 전에 관리 파일을 별도 폴더로 한 번 복사한다. 이후 Publisher는 그 독립 폴더에서 선택한 관리 파일을 스냅샷에 복사하고 해시한다.
2. 이전 서명 매니페스트와 비교해 추가, 수정, 삭제, 로더 변경, 다운로드 크기를 보여준다. 파일 수정 이후에는 다시 검사한다. 게시 대상은 검토한 스냅샷으로 고정한다.
3. 관리자가 게시를 누르면 GitHub 저장소 Contents 쓰기 권한이 있는 관리자 자격 증명으로 draft를 만든다. 자격 증명은 Windows Credential Manager를 사용한다. 단일 게시 작업자로 운영하고 기존 sequence 및 태그 충돌을 게시 직전에 재확인한다.
4. 새 자산과 서명 매니페스트를 올린다. API의 자산 상태, 크기, digest를 확인한다. digest가 없으면 인증된 자산 다운로드로 검증한다. 이전 릴리스에 남는 참조도 확인한다. 전부 통과하면 draft=false, make_latest=true로 게시한다.
5. 타임아웃 시 태그와 draft 자산을 조회해 상태를 확인한 후 같은 스냅샷으로 재개한다. 이미 게시된 태그의 콘텐츠는 교체하지 않는다. 운영 롤백은 이전 콘텐츠를 더 높은 sequence로 다시 게시한다.

## Launcher와 파일 소유권

%APPDATA%/flow_launcher/packs/sunlit-valley 아래에 state.json, blobs/, generations/, staging/, user/<dataEpoch>/를 둔다. 각 generation은 독립 gameDir을 가진다. mods, config, defaultconfigs, kubejs, resourcepacks, shaderpacks는 관리 후보이며 정확한 파일 목록을 검토한다. options.txt, optionsof.txt, servers.dat, saves/, screenshots/, logs/, crash-reports/와 모드별 사용자 데이터는 제외한다. 공유 루트 안의 모드별 개인 설정도 파일 단위로 제외한다. 기존 설치에서 알 수 없는 파일은 새 관리 파일로 자동 편입하지 않는다.

개인 데이터 디렉터리는 로컬 NTFS junction으로 연결한다. 개인 파일은 원자적 교체가 필요한 업데이트 대상과 별도로, Java 실행 직전에 영구 저장소에서 gameDir로 복사하고 종료 후 회수한다. 회수 전에 강제 종료되면 다음 실행에서 같은 generation의 파일을 먼저 회수한다. 시작 전 동기화 상태를 기록하고 충돌 시 양쪽을 보존한다. 업데이트는 미회수 세션이 해결될 때까지 보류한다. 모드가 실행 중 관리 설정을 수정해도 blob 캐시에는 전파되지 않도록 별도 복사본을 쓴다. dataEpoch가 바뀌면 새 개인 경로를 사용하고 기존 월드의 이전은 백업 및 명시적 복사로 처리한다. 팩 롤백 자체로 변경된 월드를 되돌릴 수 없으므로 호환성 변경 전 월드 백업을 수행한다.

## 원자적 활성화

1. 인스턴스별 잠금으로 업데이트와 게임 실행을 직렬화한다. Windows Job Object의 kill-on-close로 감독 프로세스가 죽으면 Java도 종료하고, 정상 종료까지 잠금을 보유한다. 시작 복구는 기존 Java가 종료됐는지 확인하고 진행한다.
2. 서명, packId, schemaVersion, 최소 런처 버전과 sequence를 검사한다. 이미 수락한 sequence보다 낮은 원격 릴리스는 거부한다. 같은 sequence의 다른 manifest digest도 거부한다.
3. 경로는 정규화된 슬래시 상대 경로만 허용한다. 절대·UNC·드라이브·ADS·빈 구성요소·점 경로·장치 예약명·끝점/공백·대소문자 충돌·파일/디렉터리 접두 충돌을 거부한다. 관리 파일 경로와 상위 경로에 재분석 지점이 없어야 한다. 사용자 junction은 별도의 로컬 정책에서만 생성하고 검증한다. 초기 형식은 파일 자산을 직접 내려받으므로 팩 압축을 풀지 않는다.
4. 같은 볼륨 staging에 새 gameDir을 준비한다. 기존 캐시와 활성 파일은 크기와 SHA-256 검증을 통과한 콘텐츠만 재사용한다. 새 자산은 임시 파일로 스트리밍하며 크기 상한과 해시를 확인한 다음 캐시에 이동한다. 저장 공간에는 staging 복사본과 다운로드 임시 파일을 포함한다. 취소나 실패 시 활성 포인터는 유지한다.
5. 모든 관리 파일과 Java/로더 준비가 끝나면 staging을 고유 generation 경로로 이동한다. 같은 디렉터리에서 state.json 임시 파일을 flush한 후 File.Replace로 활성 세대, 이전 세대, 최고 수락 sequence, manifest digest를 한 번에 전환한다. 최초 설치는 같은 볼륨의 rename을 쓴다. 실행 중인 게임은 기존 세대를 유지한다.
6. 다음 시작에서 상태 및 완료 표식을 검사한다. 활성 세대가 불완전하면 검증된 이전 세대를 복구하고 최고 수락 sequence는 유지한다. 중단된 staging과 참조 없는 세대는 잠금 아래 정리한다. 전원 중단 복구는 Windows/NTFS에서 장애 주입으로 검증한다.

## 로그인과 실행

Microsoft 개인 계정용 공개 클라이언트로 시스템 브라우저 OAuth Authorization Code + PKCE를 사용한다. FLOW 전용 앱 등록과 Xbox/Minecraft 서비스 접근을 실제 계정으로 먼저 검증한다. Microsoft 토큰을 Xbox Live와 XSTS로 교환한 다음 Minecraft 세션 토큰을 얻고 Java 이용 권한과 게임 프로필을 검사한다. 새로 고침 자격 증명은 Windows 사용자 범위 DPAPI로 저장하고 로그와 오류 내역에서 토큰을 제거한다. 로그인 취소, 접근 제한, 권한 없음, 프로필 없음, 네트워크 오류를 UI에서 구분한다.

IGameLoader의 PrepareAsync와 BuildLaunchSpecAsync에 Minecraft 버전, loader ID, 정확한 loader 버전, Java major를 전달한다. Forge 구현은 공식 게임 메타데이터와 Forge 설치 프로필의 라이브러리, 에셋, natives, 프로세서를 처리한다. 런처는 검증된 Java 17 x64로 시작하고 ProcessStartInfo.ArgumentList로 인자를 전달한다. Java 런타임, 버전 라이브러리, 자산 캐시는 팩 관리 경로 외부에 둔다. Fabric 등은 새 어댑터로 추가하며 미지원 프로필의 활성화는 거부한다.

## 완료 검증

- GDLauncher, 공식 Minecraft Launcher 및 시스템 Java가 설치되지 않은 Windows VM에서 FLOW만 배포한다. Microsoft 로그인, Java 및 Forge 다운로드, 모드팩 최초 설치, 메인 메뉴와 월드 진입을 검증한다. 재시작 후 업데이트와 기존 월드 재접속도 확인한다.
- 첫 설치, 중간 릴리스 건너뛰기, 파일 하나 변경, 모드 삭제, 캐시 손상을 검증한다.
- 서명 오류, 경로 충돌, 악성 링크, 네트워크 끊김, 디스크 부족, 프로세스 강제 종료에서 기존 세대와 개인 데이터를 검증한다.
- 게시 중 업로드 실패와 재시도를 검증하고, 게시 전 최신 릴리스가 그대로 보이는지 확인한다.
- Forge 버전과 Minecraft 버전 전환, 월드 백업 및 복구를 검증한다.

입력 필요 항목은 Sunlit Valley 원본 폴더, 정확한 Forge 빌드, 모드별 권한 목록, GitHub 저장소, 서명 인증서 및 FLOW Microsoft 앱 등록이다.




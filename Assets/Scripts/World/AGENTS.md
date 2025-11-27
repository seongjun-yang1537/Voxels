[codex] 플랫 셰이딩용 법선은 half 타입 캐스팅을 사용해 half4로 패킹해야 컴파일 오류가 나지 않는다.
[codex] Chunk는 DualContouring에서 GPU로 생성된 플랫 셰이딩 데이터와 전달된 start index를 그대로 사용하므로 CPU에서 ApplyFlatShading을 수행하지 않는다.
